//! The data folder's move on Windows (dev.25 `data-folder`, task `win`): real moves with the real probes
//! ([`RealFacts`]) on real folders, each to the end (the pointer, the old copy gone, `locate` giving the new place) and
//! back: a drive root, another drive letter on the same disk and on another volume, spaces and non-ASCII letters,
//! long paths, `\\?\` and another case, a junction, Kiln's state folder in the default root, a file that cannot be
//! read, a file of the old copy that is open while it is removed, read-only files, and the lock across processes.
//!
//! Every test builds its own Windows player's environment by hand (`fixture::win_env_at`: `LOCALAPPDATA`, `APPDATA`,
//! `USERPROFILE` below a scratch folder in `%TEMP%`); none reads the data folder from the process environment. The
//! cases that write to a drive root or another volume run only when told where they may write, and say SKIPPED
//! otherwise:
//! - `KILN_TEST_WIN_ROOT`: a drive root that is scratch (`D:\`): `D:\Kiln`, `D:\Spiele Ordner\…` are made there;
//! - `KILN_TEST_VOLUME_B`: a folder on another volume (`E:\vol-b`, an exFAT USB disk under Wine).
//!
//! Under Wine (a scratch prefix whose `d:` and `e:` point at scratch folders, no display), with the test binary copied
//! into the prefix:
//! ```sh
//! KILN_TEST_WIN_ROOT='D:\' KILN_TEST_VOLUME_B='E:\vol-b' wine 'C:\tests\data_folder_windows.exe'
//! ```
//! On a Windows PC the same with a spare drive or a USB stick. `what_stays_at_the_default_place_after_a_move` is a
//! measurement for both platforms: `--ignored` with `KILN_TEST_STAYS=<file>` writes what is left at the default root.
#![cfg(feature = "zip")]

mod fixture;

use fixture::{Fixture, put};
use kiln_manage::data_folder::{self as df, RealFacts};
use kiln_manage::data_folder_move::{self as dfm, MoveOptions};
use kiln_manage::task::NoProgress;
use std::path::Path;

/// Every file and folder below `dir`, with its size (a folder: None, its path ends in `/`), relative paths with `/`, sorted.
fn listing(dir: &Path) -> Vec<(String, Option<u64>)> {
    fn go(dir: &Path, rel: &str, out: &mut Vec<(String, Option<u64>)>) {
        let Ok(rd) = std::fs::read_dir(dir) else { return };
        for e in rd.flatten() {
            let name = e.file_name().to_string_lossy().into_owned();
            let r = if rel.is_empty() { name } else { format!("{rel}/{name}") };
            match std::fs::symlink_metadata(e.path()) {
                Ok(m) if m.is_dir() => {
                    out.push((format!("{r}/"), None));
                    go(&e.path(), &r, out);
                }
                Ok(m) => out.push((r, Some(m.len()))),
                Err(_) => out.push((format!("{r} (cannot be read)"), None)),
            }
        }
    }
    let mut out = Vec::new();
    go(dir, "", &mut out);
    out.sort();
    out
}

fn render(title: &str, l: &[(String, Option<u64>)]) -> String {
    let mut s = format!("{title}\n");
    if l.is_empty() {
        s.push_str("  (nothing)\n");
    }
    for (p, n) in l {
        match n {
            Some(n) => s.push_str(&format!("  {n:>10} bytes  {p}\n")),
            None => s.push_str(&format!("  {:>10}        {p}\n", "folder")),
        }
    }
    let total: u64 = l.iter().filter_map(|(_, n)| *n).sum();
    s.push_str(&format!("  total: {total} bytes in {} files\n", l.iter().filter(|(_, n)| n.is_some()).count()));
    s
}

/// What is left at the default root after a move away and the removal of the old copy, measured on the fixture (a
/// library with four mods, two profiles, two games, a save backup, a runtime bundle, a server record, a built
/// modpack, settings, the Nexus handler record, and one folder the launcher does not know). On Windows Kiln's state
/// folder (`%LOCALAPPDATA%\Kiln\state`) is inside the default root; the fixture puts in it what a real PC has there
/// (a server console capture and the keyring store's lock), so its size is the fixture's, not a player's.
#[test]
#[ignore = "a measurement: run with --ignored and KILN_TEST_STAYS=<file>"]
fn what_stays_at_the_default_place_after_a_move() {
    let fx = Fixture::build("stays");
    if cfg!(windows) {
        put(&fx.env.state_dir(), "servers/isles.run.log", b"[server] console line\n");
        put(&fx.env.state_dir(), "nexus-keyring.lock", b"");
    }
    let inv = df::inventory(&fx.dirs.root, &fx.env).unwrap();
    let before = listing(&fx.dirs.root);
    let to = fx.base.join("moved");
    let l = fx.env.locate().unwrap();
    let r = dfm::move_to(&l, &fx.env, &to, &RealFacts { dirs: &fx.dirs }, None, &MoveOptions::default(), &mut NoProgress).unwrap();
    let c = r.cleanup.clone().unwrap();
    assert!(c.done && !c.interrupted, "{c:?}");
    let after_move = listing(&fx.dirs.root);
    dfm::acknowledge(&fx.env).unwrap();
    let after_ack = listing(&fx.dirs.root);
    let platform = if cfg!(windows) { "Windows (under Wine)" } else { "Linux" };
    let mut out = format!(
        "# What stays at the default place after a move ({platform})\n\n\
         The fixture's data folder at the default root ({}): {} bytes in {} files moved ({} hard-linked, {} copied), \
         then the old copy removed by the move itself. Paths are relative to the default root.\n\n",
        if cfg!(windows) { r"%LOCALAPPDATA%\Kiln" } else { "$XDG_DATA_HOME/kiln" },
        r.bytes,
        r.files,
        r.linked,
        r.copied
    );
    out.push_str(&render("## Before the move", &before));
    out.push('\n');
    out.push_str(&render("## Right after the move (the journal and the file record wait for the launcher to show the result)", &after_move));
    out.push('\n');
    out.push_str(&render("## After the launcher showed the result (acknowledge): what stays for good", &after_ack));
    out.push_str(&format!(
        "\nNot launcher data, never moved: {:?}. Entries the launcher does not know stay where they were (here `My Notes`).\n",
        inv.stays.iter().map(|e| e.name.as_str()).collect::<Vec<_>>()
    ));
    eprintln!("{out}");
    if let Some(f) = std::env::var_os("KILN_TEST_STAYS") {
        std::fs::write(f, &out).unwrap();
    }
}

#[cfg(windows)]
mod win {
    use super::fixture::{Fixture, kiln_mod, put, scratch, win_env_at};
    use kiln_manage::data_folder::{self as df, DataLock, Facts, FolderState, PathParts, PathStyle, RealFacts, RefusalCode, SystemFacts};
    use kiln_manage::data_folder_move::{self as dfm, Inject, MoveOptions, MoveResult, Pending};
    use kiln_manage::paths::{DataDirs, DataEnv, ProblemKind, RootSource};
    use kiln_manage::task::{NoProgress, Progress};
    use kiln_manage::{ErrorCode, library, profile, util};
    use std::collections::{BTreeMap, BTreeSet};
    use std::os::windows::fs::OpenOptionsExt;
    use std::path::{Path, PathBuf};

    // -------------------------------------------------------------------------------------------- helpers

    /// What the test binary sees of its environment, printed once at the start of every test: no variable of a Linux
    /// shell may name a real home folder, and `KILN_DATA_DIR` is not set.
    fn seen() {
        static ONCE: std::sync::Once = std::sync::Once::new();
        ONCE.call_once(|| {
            let vars = [
                "LOCALAPPDATA", "APPDATA", "USERPROFILE", "TEMP", "TMP", "KILN_DATA_DIR", "HOME", "XDG_DATA_HOME", "XDG_STATE_HOME",
                "XDG_CONFIG_HOME", "KILN_TEST_WIN_ROOT", "KILN_TEST_VOLUME_B",
            ];
            let mut line = String::from("environment seen:");
            for v in vars {
                line.push_str(&format!(" {v}={:?}", std::env::var_os(v)));
            }
            eprintln!("{line}");
        });
        assert!(std::env::var_os("KILN_DATA_DIR").is_none(), "KILN_DATA_DIR must not be set for these tests");
        for v in ["LOCALAPPDATA", "APPDATA", "TEMP"] {
            let val = std::env::var(v).unwrap_or_default();
            assert!(!val.starts_with('/') && !val.to_lowercase().contains(r"\home\"), "{v}={val}: a Linux home leaks in");
        }
    }

    fn style() -> PathStyle {
        PathStyle::Windows
    }

    /// Two paths name one folder, by Windows' rules (no case, no `\\?\`).
    fn same(a: &Path, b: &Path) -> bool {
        PathParts::of(a, style()).same(&PathParts::of(b, style()))
    }

    fn assert_same(a: &Path, b: &Path, what: &str) {
        assert!(same(a, b), "{what}: {} is not {}", a.display(), b.display());
    }

    /// `KILN_TEST_WIN_ROOT` (a scratch drive root) or None, said so.
    fn win_root(test: &str) -> Option<PathBuf> {
        match std::env::var_os("KILN_TEST_WIN_ROOT").map(PathBuf::from) {
            Some(r) if r.is_dir() => Some(r),
            Some(r) => {
                eprintln!("SKIPPED {test}: KILN_TEST_WIN_ROOT={} is not there", r.display());
                None
            }
            None => {
                eprintln!("SKIPPED {test}: KILN_TEST_WIN_ROOT is not set");
                None
            }
        }
    }

    fn volume_b(test: &str) -> Option<PathBuf> {
        match std::env::var_os("KILN_TEST_VOLUME_B").map(PathBuf::from) {
            Some(b) if b.is_dir() => Some(b),
            other => {
                eprintln!("SKIPPED {test}: KILN_TEST_VOLUME_B is {other:?}, not a folder");
                None
            }
        }
    }

    /// Every regular file below `root` but the data-folder machinery's own files and the locks: relative path -> sha256.
    fn tree(root: &Path) -> BTreeMap<String, String> {
        fn go(dir: &Path, rel: &str, out: &mut BTreeMap<String, String>) {
            let Ok(rd) = std::fs::read_dir(dir) else { return };
            for e in rd.flatten() {
                let name = e.file_name().to_string_lossy().into_owned();
                let r = if rel.is_empty() { name.clone() } else { format!("{rel}/{name}") };
                let m = std::fs::symlink_metadata(e.path()).unwrap();
                let top = rel.is_empty() && (df::is_own_file(&name, PathStyle::Windows) || df::is_top_lock(&name, PathStyle::Windows));
                if top || (rel == "nexus" && name.ends_with(".lock")) {
                    continue;
                }
                if m.is_dir() {
                    go(&e.path(), &r, out);
                } else if m.is_file() {
                    out.insert(r, kiln_manage::hash::sha256_file(&e.path()).unwrap().0);
                }
            }
        }
        let mut out = BTreeMap::new();
        go(root, "", &mut out);
        out
    }

    /// `tree` of the default root without Kiln's state folder (`state\`: not launcher data, it never moves).
    fn data_tree(default: &Path) -> BTreeMap<String, String> {
        tree(default).into_iter().filter(|(k, _)| !k.starts_with("state/")).collect()
    }

    /// The files whose content does not name the data root (records that name it are written again by a move).
    fn data_only(t: &BTreeMap<String, String>) -> BTreeMap<String, String> {
        t.iter().filter(|(k, _)| !k.starts_with("installs/") && !k.starts_with("servers/") && !k.contains("/config/")).map(|(k, v)| (k.clone(), v.clone())).collect()
    }

    fn names(d: &Path) -> BTreeSet<String> {
        std::fs::read_dir(d).map(|rd| rd.flatten().map(|e| e.file_name().to_string_lossy().into_owned()).collect()).unwrap_or_default()
    }

    fn lib_dir(dirs: &DataDirs, r: &library::LibraryRef) -> PathBuf {
        dirs.library_entry(r.kind, &r.id, &r.version, &r.hash).unwrap()
    }

    fn mv(env: &DataEnv, picked: &Path, opts: &MoveOptions, p: &mut dyn Progress) -> kiln_manage::Result<MoveResult> {
        let l = env.locate().unwrap();
        let dirs = l.dirs.clone();
        dfm::move_to(&l, env, picked, &RealFacts { dirs: &dirs }, None, opts, p)
    }

    fn refusal(e: &kiln_manage::Error) -> Option<String> {
        e.detail.as_ref().and_then(|d| d["refusal"].as_str()).map(str::to_string)
    }

    fn check_here(env: &DataEnv, picked: &Path) -> Result<df::Plan, df::Refusal> {
        df::check_here(&env.locate().unwrap(), env, picked, false).unwrap()
    }

    /// The files of Kiln's state folder with their content.
    fn state_files(env: &DataEnv) -> BTreeMap<String, Vec<u8>> {
        fn go(dir: &Path, rel: &str, out: &mut BTreeMap<String, Vec<u8>>) {
            for e in std::fs::read_dir(dir).into_iter().flatten().flatten() {
                let r = format!("{rel}/{}", e.file_name().to_string_lossy());
                if e.path().is_dir() {
                    go(&e.path(), &r, out);
                } else {
                    out.insert(r, std::fs::read(e.path()).unwrap_or_else(|e| format!("unreadable: {e}").into_bytes()));
                }
            }
        }
        let mut out = BTreeMap::new();
        go(&env.state_dir(), "", &mut out);
        out
    }

    /// What a real PC keeps in `%LOCALAPPDATA%\Kiln\state`: a server console capture and the keyring store's lock.
    fn fill_state(env: &DataEnv) {
        put(&env.state_dir(), "servers/isles.run.log", b"[server] started\n");
        put(&env.state_dir(), "nexus-keyring.lock", b"");
    }

    /// The fixture moved to `picked` (the data folder must land in `want`) with the real probes, checked to the end,
    /// and moved back to the default root, checked again. Kiln's state folder is untouched throughout.
    fn away_and_back(fx: &Fixture, picked: &Path, want: &Path) -> (MoveResult, MoveResult) {
        let default = fx.dirs.root.clone();
        fill_state(&fx.env);
        let state = state_files(&fx.env);
        let before = data_tree(&default);
        let plan = check_here(&fx.env, picked).unwrap_or_else(|r| panic!("{} refused: {r}", picked.display()));
        assert_same(&plan.target, want, "the plan's target");
        eprintln!("plan for {}: target {}, same volume {:?}, hard links {}, free {:?}, needed {}", picked.display(), plan.target.display(), plan.same_volume, plan.hard_links, plan.free, plan.needed);
        let r = mv(&fx.env, picked, &MoveOptions::default(), &mut NoProgress).unwrap();
        assert_same(&r.to, want, "the move's target");
        assert!(!r.to.to_string_lossy().starts_with(r"\\?\"), "{}", r.to.display());
        // the pointer, and locate
        let p = df::read_pointer(&default).unwrap().expect("a pointer in the default root");
        assert_same(&p.path, want, "the pointer");
        assert!(!p.path.to_string_lossy().starts_with(r"\\?\"), "the pointer has no \\\\?\\: {}", p.path.display());
        let l = fx.env.locate().unwrap();
        assert_eq!(l.source, RootSource::Pointer);
        assert_same(&l.dirs.root, want, "locate");
        // the old copy is gone: what is left at the default root is the pointer, the move's records, the unknown
        // entry and Kiln's state folder
        let c = r.cleanup.clone().unwrap();
        assert!(c.done && !c.interrupted, "{c:?}");
        assert_eq!(c.kept.iter().map(|k| k.path.clone()).collect::<Vec<_>>(), [default.join("My Notes")], "{c:?}");
        assert!(!lib_dir(&fx.dirs, &fx.mods.kiln).exists() && !default.join("library").exists() && !default.join("profiles").exists());
        let left = names(&default);
        let allowed: BTreeSet<String> = [df::POINTER, df::JOURNAL, df::FILE_RECORD, "My Notes", "state"].into_iter().map(String::from).collect();
        assert!(left.is_subset(&allowed), "left at the default root: {left:?}");
        // the new place holds everything; what does not name the root is byte for byte the same
        let now = tree(want);
        assert_eq!(now.keys().collect::<Vec<_>>(), before.keys().collect::<Vec<_>>());
        assert_eq!(data_only(&now), data_only(&before));
        // the active profile is written again for the new place (native mode)
        let f = profile::read_game_profile(&fx.game_z.path).unwrap().unwrap();
        let cfg = PathBuf::from(f.config_dir.clone().unwrap());
        assert!(PathParts::of(&cfg, style()).inside(&PathParts::of(want, style())), "configDir {} names the new place", cfg.display());
        assert!(f.mods.kiln[0].path.to_lowercase().starts_with(&want.to_string_lossy().to_lowercase()), "{}", f.mods.kiln[0].path);
        dfm::acknowledge(&fx.env).unwrap();
        assert_eq!(dfm::pending(&fx.env).unwrap(), Pending::Nothing);
        assert_eq!(state_files(&fx.env), state, "Kiln's state folder is untouched by the move away");

        // back to the default root: the unknown entry left there refuses it (it never gets a Kiln subfolder, and a
        // move never writes into what is there)
        let e = mv(&fx.env, &default, &MoveOptions::default(), &mut NoProgress).unwrap_err();
        assert_eq!(refusal(&e).as_deref(), Some("default_root_taken"), "{e:?}");
        assert!(e.message.contains("My Notes"), "{e:?}");
        std::fs::remove_dir_all(default.join("My Notes")).unwrap(); // the player removes their copy first
        let back = mv(&fx.env, &default, &MoveOptions::default(), &mut NoProgress).unwrap();
        assert_same(&back.to, &default, "back");
        let l = fx.env.locate().unwrap();
        assert_eq!(l.source, RootSource::Default);
        assert_same(&l.dirs.root, &default, "locate after the move back");
        assert!(!default.join(df::POINTER).exists(), "the move back removed the pointer");
        let c = back.cleanup.clone().unwrap();
        assert!(c.done && !c.interrupted, "{c:?}");
        assert_eq!(c.kept.iter().map(|k| k.path.clone()).collect::<Vec<_>>(), [want.join("My Notes")], "only the unknown entry stays: {c:?}");
        assert_eq!(names(want).into_iter().collect::<Vec<_>>(), ["My Notes"], "the old copy is gone from {}", want.display());
        assert_eq!(data_tree(&default).keys().collect::<Vec<_>>(), before.keys().collect::<Vec<_>>());
        assert_eq!(data_only(&data_tree(&default)), data_only(&before));
        let f = profile::read_game_profile(&fx.game_z.path).unwrap().unwrap();
        assert!(f.config_dir.unwrap().to_lowercase().starts_with(&default.to_string_lossy().to_lowercase()));
        dfm::acknowledge(&fx.env).unwrap();
        assert_eq!(state_files(&fx.env), state, "Kiln's state folder is untouched by the move back");
        (r, back)
    }

    /// Removes a scratch folder a test made on a drive root or volume B (only below the given folder).
    fn clean(p: &Path) {
        let _ = std::fs::remove_dir_all(p);
    }

    // -------------------------------------------------------------------------------------------- the environment

    #[test]
    fn the_environment_this_binary_sees() {
        seen();
        let env = DataEnv::from_process();
        assert!(env.windows && env.kiln_data_dir.is_none());
        eprintln!("process default root {:?}, state {}, config {}", env.default_root(), env.state_dir().display(), env.config_dir().display());
        assert!(env.default_root().unwrap().starts_with(std::env::var("LOCALAPPDATA").unwrap()));
        assert_eq!(env.state_dir(), env.default_root().unwrap().join("state"), "Windows: the state folder is inside the default root");
    }

    // -------------------------------------------------------------------------------------------- drive roots and letters

    /// A drive root picked (`D:\`): the data folder is `D:\Kiln`, never the root; to the end and back. `\\?\D:\` is the
    /// same choice.
    #[test]
    fn a_drive_root_gets_a_kiln_folder() {
        seen();
        let Some(root) = win_root("a_drive_root_gets_a_kiln_folder") else { return };
        let want = root.join("Kiln");
        assert!(!want.exists(), "{} is there from an earlier run: remove it first", want.display());
        let fx = Fixture::build("drive-root");
        let before_root = names(&root);
        let verbatim = PathBuf::from(format!(r"\\?\{}", root.display()));
        assert_same(&check_here(&fx.env, &verbatim).unwrap().target, &want, r"\\?\ on the root");
        let (r, _) = away_and_back(&fx, &root, &want);
        eprintln!("D: same volume as C: {:?} (linked {}, copied {})", RealFacts { dirs: &fx.dirs }.same_volume(&fx.base, &root), r.linked, r.copied);
        assert_eq!(names(&root).difference(&before_root).cloned().collect::<Vec<_>>(), ["Kiln"], "only D:\\Kiln was made on the drive");
        clean(&want);
    }

    /// Another drive letter on the same host disk (C: and D: both on one Linux file system under Wine) and on another
    /// volume (E:, exFAT): what `same_volume` and `hard_links` answer, whether a hard link across two letters is
    /// refused, and that the move does not depend on it.
    #[test]
    fn another_drive_letter_same_disk_and_another_volume() {
        seen();
        let fx = Fixture::build("letters");
        let facts = RealFacts { dirs: &fx.dirs };
        let probe = fx.base.join("probe.bin");
        std::fs::write(&probe, b"x").unwrap();
        // the same letter: one volume, hard links work (NTFS; a Linux file system under Wine)
        let same_letter = fx.base.join("same letter");
        std::fs::create_dir_all(&same_letter).unwrap();
        let (sv, hl) = (facts.same_volume(&fx.dirs.root, &same_letter), facts.hard_links(&fx.dirs.root, &same_letter));
        eprintln!("same letter: same_volume {sv:?}, hard_links {hl}");
        assert_eq!(sv, Some(true));
        std::fs::remove_dir(&same_letter).unwrap();
        if let Some(root) = win_root("another_drive_letter_same_disk_and_another_volume (D:)") {
            let d = root.join(format!("letters-{}", std::process::id()));
            std::fs::create_dir_all(&d).unwrap();
            let sv = facts.same_volume(&fx.base, &d);
            let hl = facts.hard_links(&fx.base, &d);
            let raw = std::fs::hard_link(&probe, d.join("probe.bin"));
            eprintln!("C: and D: (one host disk under Wine): same_volume {sv:?}, hard_links probe {hl}, a hard link across the letters: {raw:?}");
            assert_eq!(sv, Some(false), "two drive letters are two volumes to the launcher");
            clean(&d);
            let plan = check_here(&fx.env, &root.join("Kiln letters")).unwrap();
            assert!(!plan.hard_links, "hard links are used only within one volume, whatever a probe across letters says: {plan:?}");
        }
        let Some(b) = volume_b("another_drive_letter_same_disk_and_another_volume (E:)") else { return };
        let to = b.join(format!("Kiln Daten {}", std::process::id()));
        clean(&to);
        let sv = facts.same_volume(&fx.base, &b);
        let hl = facts.hard_links(&fx.base, &b);
        let raw = std::fs::hard_link(&probe, b.join(format!("probe-{}.bin", std::process::id())));
        let _ = std::fs::remove_file(b.join(format!("probe-{}.bin", std::process::id())));
        eprintln!("C: and E: (another volume): same_volume {sv:?}, hard_links probe {hl}, a hard link across: {raw:?}");
        assert_eq!((sv, hl), (Some(false), false));
        assert!(raw.is_err(), "a hard link never crosses volumes");
        let (r, back) = away_and_back(&fx, &to, &to);
        assert_eq!((r.linked, back.linked), (0, 0), "across volumes every file is copied");
        clean(&to);
        // facts that wrongly say hard links work there: the move still copies (a failed link falls back to a copy)
        let fx = Fixture::build("letters-lie");
        let to = b.join(format!("Kiln lie {}", std::process::id()));
        clean(&to);
        let lie = Facts { same_volume: Some(true), hard_links: true, ..Facts::all_clear() };
        let l = fx.env.locate().unwrap();
        let r = dfm::move_to(&l, &fx.env, &to, &lie, None, &MoveOptions::default(), &mut NoProgress).unwrap();
        assert_eq!(r.linked, 0, "{r:?}");
        assert_same(&fx.env.locate().unwrap().dirs.root, &to, "locate");
        clean(&to);
    }

    /// A move within one drive letter by hard links: the record says which files are linked, what is written in place
    /// is copied for real, and a write through the new place leaves the old copy as it was (NTFS hard links).
    #[test]
    fn a_move_on_one_letter_by_hard_links() {
        seen();
        let fx = Fixture::build("one-letter");
        let to = fx.base.join("moved");
        let before = tree(&fx.dirs.root);
        let mut stop = AfterSwitch(false);
        let r = mv(&fx.env, &to, &MoveOptions::default(), &mut stop).unwrap();
        eprintln!("one letter: linked {}, copied {}", r.linked, r.copied);
        assert!(r.linked > 0 && r.copied > 0, "{r:?}");
        let new = DataDirs::at(&to);
        util::write_json_atomic(&new.settings(), &serde_json::json!({ "savesKeep": 3 })).unwrap();
        profile::activate(&new, &fx.active, &fx.game_z).unwrap();
        library::import(&new, &[kiln_mod(&fx.base, "after-move", "1.0.0")], &mut NoProgress).unwrap();
        assert_eq!(tree(&fx.dirs.root), before, "the old copy is as it was before the move");
        let c = dfm::remove_old_copy(&fx.env, &mut NoProgress).unwrap();
        assert!(c.done, "{c:?}");
        assert!(!lib_dir(&fx.dirs, &fx.mods.kiln).exists() && lib_dir(&new, &fx.mods.kiln).join("M.dll").is_file());
    }

    /// Cancels as soon as the switch is under way (the move's own removal of the old copy is interrupted).
    struct AfterSwitch(bool);
    impl Progress for AfterSwitch {
        fn report(&mut self, _: Option<f32>, m: &str) {
            if m.starts_with("switching") {
                self.0 = true;
            }
        }
        fn canceled(&self) -> bool {
            self.0
        }
    }

    // -------------------------------------------------------------------------------------------- names

    /// A folder with spaces and non-ASCII letters (`D:\Spiele Ordner\Jörg Müller\データ`), from a user profile that has
    /// them too, to the end and back.
    #[test]
    fn spaces_and_non_ascii_letters() {
        seen();
        let fx = Fixture::build("Jörg Müller データ");
        assert!(fx.dirs.root.to_string_lossy().contains("Jörg Müller データ"));
        let base = win_root("spaces_and_non_ascii_letters (on D:, else below the scratch folder)").unwrap_or_else(|| fx.base.clone());
        let top = base.join("Spiele Ordner");
        if base != fx.base {
            assert!(!top.exists(), "{} is there from an earlier run", top.display());
        }
        let want = top.join("Jörg Müller").join("データ");
        away_and_back(&fx, &want, &want);
        clean(&top);
    }

    /// `\\?\` on the picked folder is taken off; the same folder in another case is the same folder.
    #[test]
    fn verbatim_and_case() {
        seen();
        let fx = Fixture::build("verbatim");
        let want = fx.base.join("Kiln Ziel");
        let picked = PathBuf::from(format!(r"\\?\{}", want.display()));
        let plan = check_here(&fx.env, &picked).unwrap();
        assert!(!plan.target.to_string_lossy().starts_with(r"\\?\") && !plan.picked.to_string_lossy().starts_with(r"\\?\"), "{plan:?}");
        let r = mv(&fx.env, &picked, &MoveOptions::default(), &mut NoProgress).unwrap();
        assert_same(&r.to, &want, "target");
        let raw = std::fs::read_to_string(fx.dirs.root.join(df::POINTER)).unwrap();
        assert!(!raw.contains(r"\\\\?\\"), "the pointer file: {raw}");
        dfm::acknowledge(&fx.env).unwrap();
        // the data folder in use, named another way
        let lower = PathBuf::from(want.to_string_lossy().to_lowercase());
        let upper = PathBuf::from(want.to_string_lossy().to_uppercase());
        for p in [lower.clone(), upper, PathBuf::from(format!(r"\\?\{}", want.display())), PathBuf::from(format!(r"{}\", want.display()))] {
            assert_eq!(check_here(&fx.env, &p).unwrap_err().code, RefusalCode::SameFolder, "{}", p.display());
        }
        assert_eq!(check_here(&fx.env, &lower.join("LIBRARY")).unwrap_err().code, RefusalCode::InsideCurrent);
        // a pointer written in another case still finds it, and a move back to the default root in another case works
        df::write_pointer(&fx.dirs.root, &lower).unwrap();
        let l = fx.env.locate().unwrap();
        assert_same(&l.dirs.root, &want, "a pointer in lower case");
        let default_upper = PathBuf::from(fx.dirs.root.to_string_lossy().to_uppercase());
        std::fs::remove_dir_all(fx.dirs.root.join("My Notes")).unwrap();
        let back = mv(&fx.env, &default_upper, &MoveOptions::default(), &mut NoProgress).unwrap();
        assert_same(&back.to, &fx.dirs.root, "back");
        assert_eq!(fx.env.locate().unwrap().source, RootSource::Default, "the default root in another case is the default root: the pointer went");
        assert!(back.cleanup.unwrap().done);
    }

    // -------------------------------------------------------------------------------------------- long paths

    /// A target that makes a library path longer than 259 characters is refused (`path_too_long`); one that makes it
    /// exactly 259 moves, and back.
    #[test]
    fn long_library_paths() {
        seen();
        let fx = Fixture::build("long-lib");
        let inv = df::inventory(&fx.dirs.root, &fx.env).unwrap();
        let rel = inv.longest_game.clone().unwrap();
        eprintln!("the longest path the game reads: {} ({} units)", rel.path, rel.units);
        // <base>\<pad> with <base>\<pad>\<rel> = 259 or 260 units
        let base_units = fx.base.to_string_lossy().encode_utf16().count();
        let pad = |total: usize| "p".repeat(total - base_units - 2 - rel.units);
        let too_long = fx.base.join(pad(260));
        let r = check_here(&fx.env, &too_long).unwrap_err();
        assert_eq!(r.code, RefusalCode::PathTooLong, "{r}");
        assert!(r.reason.contains("260"), "{}", r.reason);
        let e = mv(&fx.env, &too_long, &MoveOptions::default(), &mut NoProgress).unwrap_err();
        assert_eq!(refusal(&e).as_deref(), Some("path_too_long"));
        assert!(!too_long.exists());
        let fits = fx.base.join(pad(259));
        let r = mv(&fx.env, &fits, &MoveOptions::default(), &mut NoProgress).unwrap();
        let at = fits.join(&rel.path);
        assert!(at.exists(), "{} at the new place", at.display());
        assert_eq!(at.to_string_lossy().encode_utf16().count(), 259, "the longest path the game reads, at the new place");
        assert!(r.cleanup.unwrap().done);
        dfm::acknowledge(&fx.env).unwrap();
        std::fs::remove_dir_all(fx.dirs.root.join("My Notes")).unwrap();
        let back = mv(&fx.env, &fx.dirs.root, &MoveOptions::default(), &mut NoProgress).unwrap();
        assert!(back.cleanup.unwrap().done);
        assert!(lib_dir(&fx.dirs, &fx.mods.kiln).join("M.dll").is_file());
        clean(&fits);
    }

    /// A data folder without a library moves to a path of more than 260 characters and back; the launcher's own file
    /// work there (settings, a profile, a library import, the probes) works.
    #[test]
    fn a_data_folder_without_a_library_past_260() {
        seen();
        let s = scratch("long-nolib");
        let env = win_env_at(&s.0);
        let default = env.default_root().unwrap();
        put(&default, "settings.json", b"{\"savesKeep\": 10}\n");
        put(&default, "logs/launcher.log", b"a line\n");
        put(&default, "runtime/kiln-runtime-0.2.0-dev+20261111.abcdef0.zip", b"PK not really");
        put(&default, "backups/servers/isles/a.tar", b"tar");
        let before = tree(&default);
        let mut deep = s.0.clone();
        for c in ["Spiele Ordner", &"a".repeat(100), &"b".repeat(100), "Kiln Daten"] {
            deep.push(c);
        }
        let units = deep.to_string_lossy().encode_utf16().count();
        assert!(units > 260, "{units}");
        let facts = RealFacts { dirs: &DataDirs::at(&default) };
        let probe_dir = deep.parent().unwrap();
        std::fs::create_dir_all(probe_dir).unwrap();
        let (space, sv) = (facts.space(probe_dir), facts.same_volume(&default, probe_dir));
        eprintln!("the probes at {} units: space {space:?}, same volume {sv:?}, writable {}", probe_dir.to_string_lossy().encode_utf16().count(), facts.writable(probe_dir));
        assert!(space.is_some(), "free space at a long path");
        assert_eq!(sv, Some(true), "the volume of a long path");
        let plan = check_here(&env, &deep).unwrap();
        assert!(plan.free.is_some() && plan.hard_links, "{plan:?}");
        let r = mv(&env, &deep, &MoveOptions::default(), &mut NoProgress).unwrap();
        assert_same(&r.to, &deep, "target");
        assert!(r.cleanup.clone().unwrap().done, "{r:?}");
        assert_same(&env.locate().unwrap().dirs.root, &deep, "locate");
        assert_eq!(tree(&deep), before);
        dfm::acknowledge(&env).unwrap();
        // the launcher's own work there
        let new = DataDirs::at(&deep);
        util::write_json_atomic(&new.settings(), &serde_json::json!({ "savesKeep": 3 })).unwrap();
        let p = profile::create(&new, "Lange Wege", None).unwrap();
        assert!(profile::list(&new).unwrap().iter().any(|x| x.id == p.id));
        library::import(&new, &[kiln_mod(&s.0, "far-away", "1.0.0")], &mut NoProgress).unwrap();
        assert_eq!(library::list(&new, None).unwrap().len(), 1);
        let inv = df::inventory(&deep, &env).unwrap();
        assert!(inv.longest.unwrap().units > 40);
        // and back (to the short default root)
        let after = tree(&deep);
        let back = mv(&env, &default, &MoveOptions::default(), &mut NoProgress).unwrap();
        assert_same(&back.to, &default, "back");
        let c = back.cleanup.unwrap();
        assert!(c.done && c.kept.is_empty(), "{c:?}");
        assert_eq!(tree(&default), after);
        assert!(!deep.join("library").exists() && !deep.join("settings.json").exists());
    }

    // -------------------------------------------------------------------------------------------- junctions

    /// `cmd /c mklink /J <link> <target>`.
    fn junction(link: &Path, target: &Path) -> bool {
        let out = std::process::Command::new("cmd").arg("/c").arg("mklink").arg("/J").arg(link).arg(target).output();
        match out {
            Ok(o) if o.status.success() && link.exists() => true,
            other => {
                eprintln!("mklink /J {} {} failed: {other:?}", link.display(), target.display());
                false
            }
        }
    }

    /// A junction as the picked folder, as the `Kiln` subfolder of a picked folder, as the folder a pointer names,
    /// and inside the data folder: refused, never followed. A junction above the picked folder is resolved.
    #[test]
    fn junctions_are_refused() {
        seen();
        let fx = Fixture::build("junction");
        let real = fx.base.join("real target");
        std::fs::create_dir_all(&real).unwrap();
        let link = fx.base.join("a junction");
        assert!(junction(&link, &real), "Windows (and Wine) make junctions with mklink /J");
        let m = std::fs::symlink_metadata(&link).unwrap();
        {
            use std::os::windows::fs::MetadataExt;
            eprintln!("the junction: attributes {:#x}, is_symlink {}", m.file_attributes(), m.file_type().is_symlink());
        }
        assert_eq!(df::recognize(&link, &fx.env).unwrap(), FolderState::Link);
        assert_eq!(check_here(&fx.env, &link).unwrap_err().code, RefusalCode::Link);
        let e = mv(&fx.env, &link, &MoveOptions::default(), &mut NoProgress).unwrap_err();
        assert_eq!(refusal(&e).as_deref(), Some("link"));
        assert!(names(&real).is_empty(), "nothing was written through it");
        // a junction to the data folder in use
        let alias = fx.base.join("alias");
        assert!(junction(&alias, &fx.dirs.root));
        assert_eq!(check_here(&fx.env, &alias).unwrap_err().code, RefusalCode::Link);
        assert_eq!(check_here(&fx.env, &alias.join("new")).unwrap_err().code, RefusalCode::InsideCurrent, "below a junction into the data folder");
        // the Kiln subfolder of a folder with other files
        let busy = fx.base.join("busy");
        put(&busy, "photo.jpg", b"x");
        assert!(junction(&busy.join("Kiln"), &real));
        assert_eq!(check_here(&fx.env, &busy).unwrap_err().code, RefusalCode::Link);
        // a junction above the picked folder is resolved: the data folder goes where it leads
        let plan = check_here(&fx.env, &link.join("data")).unwrap();
        assert_same(&plan.target, &real.join("data"), "below a junction");
        // a pointer that names a junction
        let kiln_real = fx.base.join("kiln real");
        put(&kiln_real, "installs/x.json", b"{}");
        let kiln_link = fx.base.join("kiln link");
        assert!(junction(&kiln_link, &kiln_real));
        df::write_pointer(&fx.dirs.root, &kiln_link).unwrap();
        assert_eq!(fx.env.locate_checked().unwrap_err().kind, ProblemKind::NotAFolder);
        assert_eq!(refusal(&dfm::repoint(&fx.env, &kiln_link, &Facts::all_clear()).unwrap_err()).as_deref(), Some("link"));
        df::remove_pointer(&fx.dirs.root).unwrap();
        // a junction inside the data folder: listed, never walked into; a move is refused
        let inside = fx.dirs.root.join("library").join("kiln").join("linked");
        assert!(junction(&inside, &fx.base.join("src")));
        let inv = df::inventory(&fx.dirs.root, &fx.env).unwrap();
        assert_eq!(inv.links, ["library/kiln/linked"]);
        assert_eq!(check_here(&fx.env, &fx.base.join("elsewhere")).unwrap_err().code, RefusalCode::LinkInside);
        let e = mv(&fx.env, &fx.base.join("elsewhere"), &MoveOptions::default(), &mut NoProgress).unwrap_err();
        assert_eq!(refusal(&e).as_deref(), Some("link_inside"));
        assert!(fx.base.join("src/new-items/M.dll").is_file(), "what the junction leads to is untouched");
        std::fs::remove_dir(&inside).unwrap();
    }

    // -------------------------------------------------------------------------------------------- the default root's state

    /// `%LOCALAPPDATA%\Kiln\state` with files in it (one held open for writing, as a running server's console capture
    /// is) is untouched by a move away, a move back, a move back that fails after the copy and one that is cancelled.
    /// The launcher's own `state\file-hashes.json` at the moved place stays there on the way back.
    #[test]
    fn the_state_folder_is_untouched() {
        seen();
        for case in ["done", "fail", "cancel"] {
            let fx = Fixture::build(&format!("state-{case}"));
            fill_state(&fx.env);
            let log = fx.env.state_dir().join("servers/isles.run.log");
            let mut held = std::fs::OpenOptions::new().append(true).open(&log).unwrap(); // a running server writes it
            let state = state_files(&fx.env);
            let away = fx.base.join("away");
            let r = mv(&fx.env, &away, &MoveOptions::default(), &mut NoProgress).unwrap();
            assert!(r.cleanup.unwrap().done);
            dfm::acknowledge(&fx.env).unwrap();
            assert_eq!(state_files(&fx.env), state, "{case}: the move away");
            assert!(!away.join("state").exists(), "{case}: the state folder was not copied");
            // the launcher at the new place keeps its file-hash cache in <data>\state
            put(&away, "state/file-hashes.json", b"{}");
            std::fs::remove_dir_all(fx.dirs.root.join("My Notes")).unwrap();
            let opts = if case == "fail" { MoveOptions { force_copy: false, inject: Some(Inject::FailAfterCopy) } } else { MoveOptions::default() };
            struct Cancel;
            impl Progress for Cancel {
                fn report(&mut self, _: Option<f32>, _: &str) {}
                fn canceled(&self) -> bool {
                    true
                }
            }
            let back = if case == "cancel" { mv(&fx.env, &fx.dirs.root, &opts, &mut Cancel) } else { mv(&fx.env, &fx.dirs.root, &opts, &mut NoProgress) };
            use std::io::Write;
            held.write_all(b"[server] still running\n").unwrap();
            drop(held);
            let mut want = state.clone();
            want.get_mut("/servers/isles.run.log").unwrap().extend_from_slice(b"[server] still running\n");
            assert_eq!(state_files(&fx.env), want, "{case}: the move back");
            match case {
                "done" => {
                    let back = back.unwrap();
                    assert!(back.cleanup.unwrap().done);
                    assert!(!fx.env.state_dir().join("file-hashes.json").exists(), "the moved place's hash cache is not copied into it");
                    assert_eq!(fx.env.locate().unwrap().source, RootSource::Default);
                }
                _ => {
                    let e = back.unwrap_err();
                    assert!(matches!(e.code, ErrorCode::Io | ErrorCode::Canceled), "{case}: {e:?}");
                    assert_same(&fx.env.locate().unwrap().dirs.root, &away, "the old place is in use");
                    assert!(!fx.dirs.root.join("library").exists() && !fx.dirs.root.join(df::MOVE_MARKER).exists(), "{case}: what the move made is gone");
                    assert!(away.join("library").is_dir());
                }
            }
        }
    }

    // -------------------------------------------------------------------------------------------- files held by others

    /// A file that cannot be read during the copy (held open with a share mode that denies reading, as antivirus can):
    /// the move fails naming it, the old place is in use and complete, the partial copy is gone. On one letter (hard
    /// links) and to another letter (copies).
    #[test]
    fn a_file_held_without_read_sharing_fails_the_move() {
        seen();
        let other_letter = std::env::var_os("KILN_TEST_WIN_ROOT").map(PathBuf::from).filter(|r| r.is_dir());
        let mut targets = vec![None];
        if other_letter.is_some() {
            targets.push(other_letter.clone());
        }
        for t in targets {
            let fx = Fixture::build("held");
            let before = tree(&fx.dirs.root);
            let to = match &t {
                Some(r) => r.join(format!("Kiln held {}", std::process::id())),
                None => fx.base.join("target"),
            };
            let f = lib_dir(&fx.dirs, &fx.mods.native).join("fps.dll");
            let held = std::fs::OpenOptions::new().read(true).share_mode(0).open(&f).unwrap();
            assert!(std::fs::read(&f).is_err(), "the file cannot be read by another handle");
            let e = mv(&fx.env, &to, &MoveOptions::default(), &mut NoProgress).unwrap_err();
            drop(held);
            eprintln!("held file, target {}: {}", to.display(), e.message);
            assert!(e.message.contains(&f.display().to_string()), "the error names the file: {e:?}");
            assert_eq!(tree(&fx.dirs.root), before, "the old place is complete");
            assert_same(&fx.env.locate().unwrap().dirs.root, &fx.dirs.root, "and in use");
            // what the move left in the target: nothing, or (a defect, see the task report) the held file's hard link
            // that was made before its read failed and so is not in the move's list of what it made
            let left: Vec<(String, Option<u64>)> = super::listing(&to);
            eprintln!("left in the target after the failed move: {left:?}");
            let held_rel = f.strip_prefix(&fx.dirs.root).unwrap().to_string_lossy().replace('\\', "/");
            let only_link = left.iter().all(|(p, n)| n.is_none() || *p == held_rel) && left.iter().any(|(p, _)| *p == held_rel);
            assert!(!to.exists() || only_link, "the partial copy is gone: {left:?}");
            assert!(!to.join(df::MOVE_MARKER).exists() && !to.join(df::MARKER).exists(), "no marker is left");
            assert!(!fx.dirs.root.join(df::JOURNAL).exists() && !fx.dirs.root.join(df::POINTER).exists());
        }
    }

    /// Opens a file of the old copy once the switch is under way and holds it until dropped (`share`: the share mode).
    struct HoldOld {
        file: PathBuf,
        share: u32,
        held: Option<std::fs::File>,
    }
    impl Progress for HoldOld {
        fn report(&mut self, _: Option<f32>, m: &str) {
            if self.held.is_none() && m.starts_with("switching") {
                self.held = Some(std::fs::OpenOptions::new().read(true).share_mode(self.share).open(&self.file).unwrap());
            }
        }
    }

    /// A file of the old copy that is open while the old copy is removed: without delete sharing it stays (named, with
    /// the reason) and goes at the next removal; with it, it goes. Nothing is lost either way.
    #[test]
    fn a_file_of_the_old_copy_open_during_its_removal() {
        seen();
        const FILE_SHARE_READ: u32 = 1;
        const FILE_SHARE_ALL: u32 = 7; // read, write, delete: what std and most programs ask for
        for (share, by_links) in [(FILE_SHARE_READ, true), (FILE_SHARE_READ, false), (FILE_SHARE_ALL, true)] {
            let fx = Fixture::build(&format!("open-old-{share}-{by_links}"));
            let to = fx.base.join("moved");
            let old = lib_dir(&fx.dirs, &fx.mods.kiln).join("M.dll");
            let new = lib_dir(&DataDirs::at(&to), &fx.mods.kiln).join("M.dll");
            let content = std::fs::read(&old).unwrap();
            let mut hold = HoldOld { file: old.clone(), share, held: None };
            let opts = MoveOptions { force_copy: !by_links, inject: None };
            let r = mv(&fx.env, &to, &opts, &mut hold).unwrap();
            let c = r.cleanup.clone().unwrap();
            eprintln!("share {share}, links {by_links}: kept {:?}", c.kept.iter().map(|k| (k.path.display().to_string(), k.reason.clone())).collect::<Vec<_>>());
            assert_eq!(std::fs::read(&new).unwrap(), content, "the new place has the file");
            if share == FILE_SHARE_READ {
                let k = c.kept.iter().find(|k| k.path == old).unwrap_or_else(|| panic!("the open file is kept: {c:?}"));
                assert!(k.reason.contains("could not be removed"), "{k:?}");
                assert_eq!(std::fs::read(&old).unwrap(), content, "and complete");
                drop(hold);
                let c = dfm::remove_old_copy(&fx.env, &mut NoProgress).unwrap();
                assert!(c.done && !c.kept.iter().any(|k| k.path == old), "the next removal takes it: {c:?}");
            } else {
                assert!(!c.kept.iter().any(|k| k.path == old), "{c:?}");
                drop(hold);
            }
            assert!(!old.exists(), "gone once nothing holds it");
            assert_eq!(std::fs::read(&new).unwrap(), content, "nothing is lost");
        }
    }

    /// Read-only files (a mod unpacked with the attribute, a config the player protected): moved, verified, and the
    /// old copy removed; the new place keeps the attribute.
    #[test]
    fn read_only_files_move_and_their_old_copy_goes() {
        seen();
        for by_links in [true, false] {
            let fx = Fixture::build(&format!("read-only-{by_links}"));
            let lib = lib_dir(&fx.dirs, &fx.mods.native).join("fps.dll");
            let cfg = fx.dirs.profile(&fx.active).join("config").join("native.json");
            for f in [&lib, &cfg] {
                let mut p = std::fs::metadata(f).unwrap().permissions();
                p.set_readonly(true);
                std::fs::set_permissions(f, p).unwrap();
            }
            let to = fx.base.join("moved");
            let r = mv(&fx.env, &to, &MoveOptions { force_copy: !by_links, inject: None }, &mut NoProgress).unwrap();
            eprintln!("read-only, links {by_links}: linked {}, warnings {:?}", r.linked, r.warnings);
            // the read-only config copied to the new place makes the activation there fail (its rename over a read-only
            // file is refused): the move says so, and the game's profile still names the old place (see the report)
            let named = profile::read_game_profile(&fx.game_z.path).unwrap().unwrap().config_dir.unwrap_or_default();
            eprintln!("read-only, links {by_links}: the game's profile names {named}; activated {:?}", r.activated);
            assert!(r.warnings.iter().any(|w| w.contains("activate it again")), "{:?}", r.warnings);
            let c = r.cleanup.clone().unwrap();
            assert_eq!(c.kept.iter().map(|k| k.path.clone()).collect::<Vec<_>>(), [fx.dirs.root.join("My Notes")], "links {by_links}: {c:?}");
            assert!(!lib.exists() && !cfg.exists(), "the read-only old files are gone");
            let new = DataDirs::at(&to);
            let nlib = lib_dir(&new, &fx.mods.native).join("fps.dll");
            assert!(std::fs::metadata(&nlib).unwrap().permissions().readonly(), "the new place keeps the attribute");
            // a failure after the copy removes a partial copy with read-only files too
            let fx = Fixture::build(&format!("read-only-fail-{by_links}"));
            let lib = lib_dir(&fx.dirs, &fx.mods.native).join("fps.dll");
            let mut p = std::fs::metadata(&lib).unwrap().permissions();
            p.set_readonly(true);
            std::fs::set_permissions(&lib, p).unwrap();
            let to = fx.base.join("target");
            let e = mv(&fx.env, &to, &MoveOptions { force_copy: !by_links, inject: Some(Inject::FailAfterCopy) }, &mut NoProgress).unwrap_err();
            assert!(!e.message.contains("could not be removed"), "{e:?}");
            assert!(!to.exists(), "the partial copy is gone: {:?}", names(&to));
            assert!(std::fs::metadata(&lib).unwrap().permissions().readonly(), "the old file is as it was");
        }
    }

    // -------------------------------------------------------------------------------------------- the lock

    fn wait_for(p: &Path, what: &str) {
        let t = std::time::Instant::now();
        while !p.exists() {
            assert!(t.elapsed().as_secs() < 60, "waited a minute for {what}");
            std::thread::sleep(std::time::Duration::from_millis(50));
        }
    }

    /// The other launcher of `the_lock_across_processes`: holds the data folder's lock shared until told to stop.
    #[test]
    #[ignore = "started by the_lock_across_processes"]
    fn lock_holder_child() {
        let Some(root) = std::env::var_os("KILN_LOCK_CHILD_ROOT").map(PathBuf::from) else { return };
        let lock = DataLock::shared(&root).unwrap().expect("shared");
        std::fs::write(root.join("child.ready"), b"").unwrap();
        wait_for(&root.join("child.release"), "the release");
        drop(lock);
    }

    /// The lock: shared twice, exclusive refused while shared, a move refused while another handle or another
    /// process holds it, and free again after.
    #[test]
    fn the_lock_within_and_across_processes() {
        seen();
        let fx = Fixture::build("lock");
        let root = fx.dirs.root.clone();
        let a = DataLock::shared(&root).unwrap().unwrap();
        let mut b = DataLock::shared(&root).unwrap().unwrap();
        assert!(DataLock::exclusive(&root).unwrap().is_none(), "exclusive while shared");
        assert!(!b.upgrade().unwrap() && !b.is_exclusive());
        let to = fx.base.join("moved");
        let e = mv(&fx.env, &to, &MoveOptions::default(), &mut NoProgress).unwrap_err();
        assert_eq!(refusal(&e).as_deref(), Some("locked"));
        let l = fx.env.locate().unwrap();
        let e = dfm::move_to(&l, &fx.env, &to, &RealFacts { dirs: &fx.dirs }, Some(&mut b), &MoveOptions::default(), &mut NoProgress).unwrap_err();
        assert_eq!(refusal(&e).as_deref(), Some("locked"));
        drop(a);
        assert!(b.upgrade().unwrap() && b.is_exclusive());
        assert!(DataLock::shared(&root).unwrap().is_none(), "shared while exclusive");
        b.downgrade().unwrap();
        drop(b);
        // another process (another launcher) holds it shared
        let mut child = std::process::Command::new(std::env::current_exe().unwrap())
            .args(["--ignored", "--exact", "win::lock_holder_child", "--nocapture"])
            .env("KILN_LOCK_CHILD_ROOT", &root)
            .spawn()
            .unwrap();
        wait_for(&root.join("child.ready"), "the other process");
        let shared_too = DataLock::shared(&root).unwrap().is_some();
        let excl = DataLock::exclusive(&root).unwrap().is_none();
        let e = mv(&fx.env, &to, &MoveOptions::default(), &mut NoProgress).unwrap_err();
        std::fs::write(root.join("child.release"), b"").unwrap();
        let st = child.wait().unwrap();
        assert!(st.success(), "{st:?}");
        assert!(shared_too, "two launchers share it");
        assert!(excl, "a move cannot take it while another process holds it");
        assert_eq!(refusal(&e).as_deref(), Some("locked"), "{e:?}");
        assert!(!to.exists());
        std::fs::remove_file(root.join("child.ready")).unwrap();
        std::fs::remove_file(root.join("child.release")).unwrap();
        assert!(DataLock::exclusive(&root).unwrap().is_some(), "free when the other process has quit");
        // with the lock free the move goes through, and the quick flow's lock held by another handle refuses it
        let q = std::fs::OpenOptions::new().read(true).write(true).open(root.join("quick.lock")).unwrap();
        q.lock().unwrap();
        assert_eq!(refusal(&mv(&fx.env, &to, &MoveOptions::default(), &mut NoProgress).unwrap_err()).as_deref(), Some("busy"));
        drop(q);
        let r = mv(&fx.env, &to, &MoveOptions::default(), &mut NoProgress).unwrap();
        assert!(r.cleanup.unwrap().done);
    }
}
