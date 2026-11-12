//! The data folder as a whole (dev.25 `data-folder`, task core-1): what a folder is (`recognize`), what the data
//! folder holds (`inventory`), every refusal of a picked folder and the plans (`check`), the system's probes
//! (`RealFacts`), and the list of files that record the data root's absolute path (the fixture: tests/fixture/mod.rs).
//! The Windows path rules run here too, through `DataEnv { windows: true, .. }` over Windows path strings.
//!
//! The file builds for Windows too (run under Wine, see tests/data_folder_windows.rs): the parts that need a symlink,
//! Unix permissions (a folder made unreadable with `chmod`), a named pipe, a Wine prefix with `Z:` or Unix modes are
//! Unix only, each marked where it is; on Windows the fixture is a Windows player's (`fixture::win_env_at`).
#![cfg(feature = "zip")]

mod fixture;

use fixture::{Fixture, env_at, put, scratch, sub};
use kiln_manage::data_folder::{
    self as df, Action, CheckInput, Evidence, Facts, FolderState, Inventory, LongestPath, PathParts, PathStyle, Plan, RealFacts, Refusal,
    RefusalCode, Space, SystemFacts,
};
use kiln_manage::game::{GameInstall, GameKind, PathMode, Platform};
use kiln_manage::paths::{DataDirs, DataEnv, RootSource};
#[cfg(unix)]
use std::collections::{BTreeMap, BTreeSet};
#[cfg(unix)]
use std::os::unix::fs::{PermissionsExt, symlink};
use std::path::{Path, PathBuf};

/// A small data folder at the default root of a scratch environment.
struct Light {
    _s: fixture::Scratch,
    base: PathBuf,
    env: DataEnv,
    current: PathBuf,
    inv: Inventory,
}

fn light(tag: &str) -> Light {
    let s = scratch(tag);
    let base = s.0.clone();
    let env = env_at(&base);
    let current = env.default_root().unwrap();
    put(&current, "library/kiln/new-items/1.0.0+0badc0de/M.dll", b"mod");
    put(&current, "settings.json", b"{}\n");
    for d in [env.state_dir(), env.config_dir()] {
        std::fs::create_dir_all(d).unwrap();
    }
    let inv = df::inventory(&current, &env).unwrap();
    Light { _s: s, base, env, current, inv }
}

fn input<'a>(l: &'a Light, picked: &'a Path) -> CheckInput<'a> {
    CheckInput { current: &l.current, source: RootSource::Default, picked, env: &l.env, inventory: &l.inv, installs: &[], servers: &[], locked: false }
}

fn check(l: &Light, picked: &Path, facts: &Facts) -> Result<Plan, Refusal> {
    df::check(&input(l, picked), facts)
}

fn code(r: Result<Plan, Refusal>) -> RefusalCode {
    match r {
        Err(r) => r.code,
        Ok(p) => panic!("expected a refusal, got {p:#?}"),
    }
}

fn names(v: &[df::EntrySize]) -> Vec<&str> {
    v.iter().map(|e| e.name.as_str()).collect()
}

fn install_at(dir: &Path, prefix: Option<PathBuf>) -> GameInstall {
    GameInstall {
        id: format!("g-{}", dir.file_name().unwrap().to_string_lossy()),
        kind: GameKind::Client,
        path: dir.to_path_buf(),
        exe: "enshrouded.exe".into(),
        app_id: kiln_manage::CLIENT_APP_ID,
        library: None,
        platform: Platform::Proton,
        path_mode: if prefix.is_some() { PathMode::Z } else { PathMode::Copy },
        prefix,
        build_id: None,
        exe_sha256: None,
        steam_flavor: None,
        source: "manual".into(),
    }
}

#[cfg(unix)]
fn chmod(p: &Path, mode: u32) {
    std::fs::set_permissions(p, std::fs::Permissions::from_mode(mode)).unwrap();
}

// ------------------------------------------------------------------------------------------------ recognition

#[test]
fn recognizes_missing_files_links_and_empty_folders() {
    let s = scratch("recognize");
    let env = env_at(&s.0);
    let r = |p: &Path| df::recognize(p, &env).unwrap();
    assert_eq!(r(&s.0.join("missing")), FolderState::Missing);
    put(&s.0, "file.txt", b"x");
    assert_eq!(r(&s.0.join("file.txt")), FolderState::NotAFolder);
    assert_eq!(r(&s.0.join("file.txt/below")), FolderState::Missing, "a path below a file is not there");
    let d = s.0.join("folder");
    std::fs::create_dir_all(&d).unwrap();
    assert_eq!(r(&d), FolderState::Empty);
    #[cfg(unix)] // a link: tests/data_folder_windows.rs makes a junction
    {
        symlink(&d, s.0.join("link")).unwrap();
        assert_eq!(r(&s.0.join("link")), FolderState::Link);
    }
    // what a default root keeps after a move counts as empty
    for f in [df::POINTER, df::JOURNAL, &format!("{}.tmp", df::POINTER), &format!("{}.tmp", df::JOURNAL), "quick.lock", df::LOCK] {
        put(&d, f, b"");
    }
    assert_eq!(r(&d), FolderState::Empty);
    // only the two lock files by their names: another `*.lock` is the player's, a `nexus/` is an entry a move would write
    put(&d, "notes.lock", b"");
    assert_eq!(r(&d), FolderState::Other { entry: "notes.lock".into() });
    std::fs::remove_file(d.join("notes.lock")).unwrap();
    put(&d, "nexus/instance.lock", b"");
    assert_eq!(r(&d), FolderState::Other { entry: "nexus".into() });
    std::fs::remove_dir_all(d.join("nexus")).unwrap();
    // the state folder is a leftover only in the default root (Windows: the_windows_default_root_keeps_its_state)
    put(&d, "state/a.run.log", b"");
    assert_eq!(r(&d), FolderState::Other { entry: "state".into() });
    // the first entry that is not the launcher's says what it is
    put(&d, "Photos/a.jpg", b"");
    assert_eq!(r(&d), FolderState::Other { entry: "Photos".into() });
    // unreadable: an error, not a guess (Unix permissions)
    #[cfg(unix)]
    {
        let locked = s.0.join("locked");
        put(&locked, "x", b"");
        chmod(&locked, 0o000);
        let e = df::recognize(&locked, &env);
        chmod(&locked, 0o755);
        assert!(e.is_err(), "{e:?}");
    }
}

#[test]
fn a_kiln_data_folder_by_its_marker_or_its_layout() {
    let s = scratch("layout");
    let env = env_at(&s.0);
    let case = |name: &str, files: &[&str]| -> FolderState {
        let d = s.0.join(name);
        std::fs::create_dir_all(&d).unwrap();
        for f in files {
            put(&d, f, b"x");
        }
        df::recognize(&d, &env).unwrap()
    };
    let kiln = |by| FolderState::Kiln { by };
    let other = |e: &str| FolderState::Other { entry: e.into() };
    assert_eq!(case("lib", &["library/kiln/a/1.0.0+00000000/M.dll"]), kiln(Evidence::Library));
    assert_eq!(case("lib-engine", &["library/shroudforge/a/1.0.0+00000000/mod.json"]), kiln(Evidence::Library));
    assert_eq!(case("prof", &["profiles/ember-run/meta.json"]), kiln(Evidence::Profiles));
    assert_eq!(case("inst", &["installs/0b1c.json"]), kiln(Evidence::Installs));
    assert_eq!(case("rt", &["runtime/kiln-runtime-0.2.0-dev+20261111.abcdef0.zip"]), kiln(Evidence::Runtime));
    assert_eq!(case("marker", &[df::MARKER]), kiln(Evidence::Marker));
    assert_eq!(case("with-unknown", &["library/emf/x/y", "Photos/a.jpg"]), kiln(Evidence::Library), "unknown entries may sit next to it");
    // common names alone are not enough
    assert_eq!(case("weak", &["settings.json", "logs/launcher.log", "state/file-hashes.json"]), other("logs"));
    assert_eq!(case("books", &["library/books/a.epub", "Readme.txt"]), other("Readme.txt"));
    assert_eq!(case("prof-other", &["profiles/p/other.json"]), other("profiles"));
    assert_eq!(case("rt-other", &["runtime/other.zip"]), other("runtime"));
    assert_eq!(case("inst-other", &["installs/readme.txt"]), other("installs"));
    if cfg!(windows) {
        assert_eq!(case("case", &["Library/Kiln/a/M.dll"]), kiln(Evidence::Library), "Windows names have no case");
    } else {
        assert_eq!(case("case", &["Library/Kiln/a/M.dll"]), other("Library"), "Linux names count their case");
    }
    // a library that is a link is not followed
    #[cfg(unix)]
    {
        let d = s.0.join("linked");
        std::fs::create_dir_all(&d).unwrap();
        symlink(s.0.join("lib/library"), d.join("library")).unwrap();
        assert_eq!(df::recognize(&d, &env).unwrap(), other("library"));
    }
}

#[test]
fn the_windows_default_root_keeps_its_state() {
    let s = scratch("win-state");
    let env = DataEnv { windows: true, local_app_data: Some(s.0.join("lad").into()), ..Default::default() };
    let root = env.default_root().unwrap();
    assert_eq!(env.state_dir(), root.join("state"));
    put(&root, "state/server.run.log", b"console");
    put(&root, "state/nexus-oauth.lock", b"");
    put(&root, df::POINTER, b"{}");
    assert_eq!(df::recognize(&root, &env).unwrap(), FolderState::Empty, "pointer and state folder: the default root after a move");
    assert_eq!(df::own_entries(&root, &env), ["state"], "a move never copies into it, replaces or removes it");
    let other = s.0.join("elsewhere");
    put(&other, "state/server.run.log", b"console");
    assert_eq!(df::recognize(&other, &env).unwrap(), FolderState::Other { entry: "state".into() });
    // Windows compares names without case
    put(&other, "LIBRARY/Kiln/a/M.dll", b"x");
    assert_eq!(df::recognize(&other, &env).unwrap(), FolderState::Kiln { by: Evidence::Library });
    // the inventory of the default root leaves the pointer and state\ where they are
    put(&root, "library/kiln/a/1.0.0+00000000/M.dll", b"m");
    let inv = df::inventory(&root, &env).unwrap();
    assert_eq!(names(&inv.stays), [df::POINTER, "state"]);
    assert_eq!(names(&inv.known), ["library"]);
    assert_eq!((inv.bytes, inv.files), (1, 1));
    assert!(inv.locks.is_empty(), "a lock in state\\ is not the data folder's: {:?}", inv.locks);
}

// ------------------------------------------------------------------------------------------------ inventory

/// Bytes and regular files below `dir`, links not followed, `skip` (relative paths) left out: the inventory's totals
/// counted another way.
#[cfg(unix)]
fn count(dir: &Path, rel: &str, skip: &[&str]) -> (u64, u64) {
    let (mut b, mut f) = (0, 0);
    for e in std::fs::read_dir(dir).unwrap().flatten() {
        let name = e.file_name().to_string_lossy().into_owned();
        let r = if rel.is_empty() { name } else { format!("{rel}/{name}") };
        let m = std::fs::symlink_metadata(e.path()).unwrap();
        if m.is_dir() {
            let (x, y) = count(&e.path(), &r, skip);
            b += x;
            f += y;
        } else if m.is_file() && !skip.contains(&r.as_str()) {
            b += m.len();
            f += 1;
        }
    }
    (b, f)
}

#[test]
#[cfg(unix)] // a symlink and a named pipe inside the folder (Windows: a junction, tests/data_folder_windows.rs)
fn the_inventory_splits_known_unknown_locks_and_links() {
    let fx = Fixture::build("inventory");
    let root = &fx.dirs.root;
    put(root, "nexus/instance.lock", b"");
    symlink(fx.base.join("src"), root.join("library/kiln/linked")).unwrap();
    // a special file: a named pipe
    {
        use std::os::unix::ffi::OsStrExt;
        let fifo = std::ffi::CString::new(root.join("My Notes/pipe").as_os_str().as_bytes()).unwrap();
        // SAFETY: a NUL-terminated path.
        assert_eq!(unsafe { libc::mkfifo(fifo.as_ptr(), 0o600) }, 0);
    }
    let inv = df::inventory(root, &fx.env).unwrap();
    assert_eq!(names(&inv.known), ["installs", "library", "modpacks", "nexus", "profiles", "runtime", "servers", "settings.json"]);
    assert_eq!(names(&inv.unknown), ["My Notes"]);
    assert_eq!(inv.unknown_names(), ["My Notes"]);
    assert_eq!((inv.unknown[0].files, inv.unknown[0].bytes), (1, 22));
    assert!(inv.stays.is_empty(), "{:?}", inv.stays);
    assert_eq!(inv.locks, ["nexus/instance.lock", "quick.lock"]);
    assert_eq!(inv.links, ["library/kiln/linked"]);
    assert_eq!(inv.special, ["My Notes/pipe"]);
    let (b, f) = count(root, "", &["quick.lock", "nexus/instance.lock"]);
    assert_eq!((inv.bytes, inv.files), (b, f), "every regular file but the locks, links not followed");
    assert_eq!(inv.known.iter().chain(&inv.unknown).map(|e| (e.bytes, e.files)).fold((0, 0), |a, x| (a.0 + x.0, a.1 + x.1)), (b, f));
    let lib = inv.known.iter().find(|e| e.name == "library").unwrap();
    assert_eq!(lib.files, count(&root.join("library"), "library", &[]).1);
    let lg = inv.longest_game.clone().unwrap();
    assert!(lg.path.starts_with("library/") || lg.path.starts_with(&format!("profiles/{}/config", fx.active)), "{lg:?}");
    assert_eq!(lg.units, lg.path.encode_utf16().count());
    assert!(inv.longest.clone().unwrap().units >= lg.units);
    // the data root itself must be a real folder
    symlink(root, fx.base.join("root-link")).unwrap();
    assert!(df::inventory(&fx.base.join("root-link"), &fx.env).is_err());
}

// ------------------------------------------------------------------------------------------------ refusals

#[test]
fn refused_for_the_environment_the_lock_and_what_runs() {
    let l = light("env");
    let p = l.base.join("new place");
    let all = Facts::all_clear();
    let mut i = input(&l, &p);
    i.source = RootSource::Env;
    assert_eq!(code(df::check(&i, &all)), RefusalCode::EnvOverride);
    let mut i = input(&l, &p);
    i.locked = true;
    assert_eq!(code(df::check(&i, &all)), RefusalCode::Locked);
    let f = Facts { game_running: Some("enshrouded.exe is running (pid 7)".into()), ..Facts::all_clear() };
    let r = check(&l, &p, &f).unwrap_err();
    assert_eq!(r.code, RefusalCode::GameRunning);
    assert!(r.reason.contains("pid 7"), "{}", r.reason);
    assert_eq!(r.clone().into_error().code, kiln_manage::ErrorCode::GameRunning);
    let f = Facts { server_running: Some("Isles".into()), ..Facts::all_clear() };
    assert_eq!(code(check(&l, &p, &f)), RefusalCode::ServerRunning);
    // a pointer is a way the folder was found that may be changed
    let mut i = input(&l, &p);
    i.source = RootSource::Pointer;
    assert_eq!(df::check(&i, &all).unwrap().action, Action::Move);
}

#[test]
fn refused_where_the_current_folder_is() {
    let l = light("current");
    let all = Facts::all_clear();
    assert_eq!(code(check(&l, &l.current, &all)), RefusalCode::SameFolder);
    // its parent holds other files: the parent's `kiln` subfolder is the current folder
    assert_eq!(code(check(&l, l.current.parent().unwrap(), &all)), RefusalCode::SameFolder);
    assert_eq!(code(check(&l, &l.current.join("library"), &all)), RefusalCode::InsideCurrent);
    assert_eq!(code(check(&l, &l.current.join("not yet there"), &all)), RefusalCode::InsideCurrent);
    // through a link: the link itself is refused as one, a folder below it is inside the current folder
    #[cfg(unix)]
    {
        symlink(&l.current, l.base.join("alias")).unwrap();
        assert_eq!(code(check(&l, &l.base.join("alias"), &all)), RefusalCode::Link);
        assert_eq!(code(check(&l, &l.base.join("alias/new"), &all)), RefusalCode::InsideCurrent);
    }
    // a Kiln data folder that holds the current one
    let outer = l.base.join("outer");
    let inner = outer.join("inner");
    put(&inner, "library/kiln/a/1.0.0+00000000/M.dll", b"m");
    put(&outer, "installs/x.json", b"{}");
    let inv = df::inventory(&inner, &l.env).unwrap();
    let mut i = input(&l, &outer);
    i.current = &inner;
    i.inventory = &inv;
    assert_eq!(code(df::check(&i, &all)), RefusalCode::ContainsCurrent);
}

#[test]
fn refused_inside_a_game_a_server_or_kilns_own_folders() {
    let l = light("games");
    let all = Facts::all_clear();
    // a known install (its exe may be missing: a drive that is not there)
    let known = l.base.join("Known Game");
    std::fs::create_dir_all(&known).unwrap();
    let installs = [install_at(&known, None)];
    let p = known.join("kiln data");
    let mut i = input(&l, &p);
    i.installs = &installs;
    assert_eq!(code(df::check(&i, &all)), RefusalCode::InsideGame);
    assert!(check(&l, &p, &all).is_ok(), "without the record it is a folder like any other");
    // any folder above that holds the client's or the server's exe
    for exe in ["enshrouded.exe", "enshrouded_server.exe"] {
        let g = l.base.join(format!("game of {exe}"));
        put(&g, exe, b"MZ");
        assert_eq!(code(check(&l, &g.join("mods/kiln"), &all)), RefusalCode::InsideGame, "{exe}");
        assert_eq!(code(check(&l, &g, &all)), RefusalCode::InsideGame, "{exe}: the game folder itself");
    }
    // a known server's folder
    let srv = l.base.join("srv");
    std::fs::create_dir_all(&srv).unwrap();
    let servers = [srv.clone()];
    let p = srv.join("data");
    let mut i = input(&l, &p);
    i.servers = &servers;
    assert_eq!(code(df::check(&i, &all)), RefusalCode::InsideServer);
    assert!(check(&l, &p, &all).is_ok());
    // Kiln's own state and settings folders (Windows: the state folder is inside the default root, the current folder)
    let in_state = if cfg!(windows) { RefusalCode::InsideCurrent } else { RefusalCode::InsideKilnState };
    assert_eq!(code(check(&l, &l.env.state_dir().join("x"), &all)), in_state);
    assert_eq!(code(check(&l, &l.env.config_dir(), &all)), RefusalCode::InsideKilnState);
}

#[test]
fn refused_for_the_paths_form_or_what_is_there() {
    let l = light("form");
    let all = Facts::all_clear();
    assert_eq!(code(check(&l, Path::new("relative/kiln"), &all)), RefusalCode::NotAbsolute);
    assert_eq!(code(check(&l, &l.base.join("a/../b"), &all)), RefusalCode::ParentTraversal);
    put(&l.base, "file.txt", b"x");
    assert_eq!(code(check(&l, &l.base.join("file.txt"), &all)), RefusalCode::NotAFolder);
    let r = check(&l, &l.base.join("file.txt/below"), &all).unwrap_err();
    assert_eq!(r.code, RefusalCode::NotAFolder, "a file in the way: {}", r.reason);
    let busy = l.base.join("busy");
    put(&busy, "photo.jpg", b"x");
    #[cfg(unix)]
    {
        let real = l.base.join("real");
        std::fs::create_dir_all(&real).unwrap();
        symlink(&real, l.base.join("linked")).unwrap();
        assert_eq!(code(check(&l, &l.base.join("linked"), &all)), RefusalCode::Link);
        // the `kiln` subfolder of a folder with other files: a link, a file, a folder of someone else's
        symlink(&real, busy.join("kiln")).unwrap();
        assert_eq!(code(check(&l, &busy, &all)), RefusalCode::Link);
        std::fs::remove_file(busy.join("kiln")).unwrap();
    }
    put(&busy, sub(), b"a file");
    assert_eq!(code(check(&l, &busy, &all)), RefusalCode::NotAFolder);
    std::fs::remove_file(busy.join(sub())).unwrap();
    put(&busy, &format!("{}/other.txt", sub()), b"x");
    let r = check(&l, &busy, &all).unwrap_err();
    assert_eq!(r.code, RefusalCode::KilnSubfolderTaken);
    assert!(r.reason.contains(&busy.join(sub()).display().to_string()), "{}", r.reason);
    // a folder Kiln cannot read (Unix permissions)
    #[cfg(unix)]
    {
        let locked = l.base.join("locked");
        put(&locked, "x", b"");
        chmod(&locked, 0o000);
        let r = check(&l, &locked, &all);
        chmod(&locked, 0o755);
        assert_eq!(code(r), RefusalCode::Unreadable);
    }
}

#[test]
fn refused_without_write_access_or_space() {
    let l = light("space");
    let p = l.base.join("new place");
    let all = Facts::all_clear();
    let r = check(&l, &p, &Facts { writable: false, ..Facts::all_clear() }).unwrap_err();
    assert_eq!(r.code, RefusalCode::NotWritable);
    assert!(r.reason.contains(&l.base.display().to_string()), "the nearest folder that exists is probed: {}", r.reason);
    let needed = df::needed_bytes(l.inv.bytes, l.inv.files, 4096);
    let with = |free: u64, unit: u64, same: Option<bool>, links: bool| Facts {
        space: Some(Space { free, unit }),
        same_volume: same,
        hard_links: links,
        ..Facts::all_clear()
    };
    let r = check(&l, &p, &with(needed - 1, 4096, Some(false), false)).unwrap_err();
    assert_eq!(r.code, RefusalCode::TooSmall);
    assert!(r.reason.contains("free"), "{}", r.reason);
    assert_eq!(check(&l, &p, &with(needed, 4096, Some(false), false)).unwrap().needed, needed);
    // a bigger allocation unit (exFAT) needs more
    assert_eq!(code(check(&l, &p, &with(needed, 128 * 1024, Some(false), false))), RefusalCode::TooSmall);
    // hard links on one volume need room only for what is written in place (copied for real) and the margin; one
    // volume without hard links (exFAT) is a copy
    let linked = df::needed_bytes(l.inv.in_place_bytes, l.inv.in_place_files, 4096);
    assert!(linked < needed);
    let plan = check(&l, &p, &with(linked, 4096, Some(true), true)).unwrap();
    assert_eq!((plan.needed, plan.hard_links, plan.same_volume), (linked, true, Some(true)));
    assert_eq!(code(check(&l, &p, &with(linked - 1, 4096, Some(true), true))), RefusalCode::TooSmall);
    assert_eq!(code(check(&l, &p, &with(linked, 4096, Some(true), false))), RefusalCode::TooSmall);
    assert_eq!(code(check(&l, &p, &with(linked, 4096, Some(false), true))), RefusalCode::TooSmall, "hard links only count on one volume");
    // unknown free space is not a refusal
    let plan = check(&l, &p, &Facts { space: None, ..Facts::all_clear() }).unwrap();
    assert_eq!((plan.free, plan.needed), (None, needed));
    // `use` copies nothing
    let other = l.base.join("other kiln");
    put(&other, "installs/x.json", b"{}");
    let plan = check(&l, &other, &with(0, 4096, Some(false), false)).unwrap();
    assert_eq!((plan.action, plan.bytes, plan.files, plan.needed), (Action::Use, 0, 0, 0));
    assert!(check(&l, &other, &all).unwrap().unknown.is_empty());
}

/// The Windows rules over Windows path strings (nothing of them exists on this disk): network paths, `\\?\`,
/// comparison without case, a drive root, spaces and non-ASCII letters, long paths.
#[test]
fn windows_forms_through_the_whole_check() {
    let env = DataEnv {
        windows: true,
        local_app_data: Some(r"C:\Users\Jörg Müller\AppData\Local".into()),
        app_data: Some(r"C:\Users\Jörg Müller\AppData\Roaming".into()),
        user_profile: Some(r"C:\Users\Jörg Müller".into()),
        ..Default::default()
    };
    let current = PathBuf::from(r"C:\Users\Jörg Müller\AppData\Local\Kiln");
    let rel = "library/kiln/new-items/1.0.0+3f2a9c1d/Assets/Textures/frost.dds";
    let inv = Inventory {
        root: current.clone(),
        known: vec![],
        unknown: vec![],
        stays: vec![],
        locks: vec![],
        caches: vec![],
        links: vec![],
        special: vec![],
        bytes: 10,
        files: 1,
        longest: None,
        longest_game: Some(LongestPath { path: rel.into(), units: rel.len() }),
        in_place_bytes: 0,
        in_place_files: 0,
    };
    let all = Facts::all_clear();
    let go = |picked: &str| {
        df::check(
            &CheckInput { current: &current, source: RootSource::Default, picked: Path::new(picked), env: &env, inventory: &inv, installs: &[], servers: &[], locked: false },
            &all,
        )
    };
    let target = |picked: &str| PathParts::of(&go(picked).unwrap().target, PathStyle::Windows).text();
    assert_eq!(code(go(r"\\nas\games\Kiln")), RefusalCode::NetworkPath);
    assert_eq!(code(go(r"\\?\UNC\nas\games")), RefusalCode::NetworkPath);
    assert_eq!(code(go("C:Kiln")), RefusalCode::NotAbsolute);
    assert_eq!(code(go(r"D:\Games\..\Kiln")), RefusalCode::ParentTraversal);
    assert_eq!(code(go(r"c:\users\jörg müller\appdata\local\kiln")), RefusalCode::SameFolder);
    assert_eq!(code(go(r"\\?\C:\USERS\Jörg Müller\AppData\Local\Kiln\Library")), RefusalCode::InsideCurrent);
    assert_eq!(code(go(r"C:\Users\Jörg Müller\AppData\Roaming\Kiln\instances")), RefusalCode::InsideKilnState);
    // a drive root gets a Kiln subfolder; `\\?\` is taken off; spaces and non-ASCII letters pass
    assert_eq!(target(r"D:\"), r"D:\Kiln");
    assert_eq!(target(r"\\?\D:\"), r"D:\Kiln");
    assert_eq!(target(r"\\?\E:\Games"), r"E:\Games");
    assert_eq!(target(r"E:\Spiele Ordner\Jörg Müller\データ"), r"E:\Spiele Ordner\Jörg Müller\データ");
    // what the game reads may be 259 characters long; a longer path is refused, never an error of the check
    let n = df::WINDOWS_MAX_PATH - 3 - 1 - rel.len();
    assert_eq!(go(&format!(r"D:\{}", "a".repeat(n))).unwrap().action, Action::Move);
    let r = go(&format!(r"D:\{}", "a".repeat(n + 1))).unwrap_err();
    assert_eq!(r.code, RefusalCode::PathTooLong);
    assert!(r.reason.contains("260"), "{}", r.reason);
    assert_eq!(code(go(&format!(r"D:\{}", "b".repeat(300)))), RefusalCode::PathTooLong);
    assert_eq!(code(go(&format!(r"\\?\D:\{}", "c".repeat(32_000)))), RefusalCode::PathTooLong);
}

// ------------------------------------------------------------------------------------------------ plans

#[test]
fn plans_for_a_move_or_a_folder_to_use() {
    let l = light("plans");
    let all = Facts::all_clear();
    // a folder that is not there yet becomes the data folder
    let p = l.base.join("Neuer Ordner ü");
    let plan = check(&l, &p, &all).unwrap();
    assert_eq!((plan.action, &plan.picked, &plan.target), (Action::Move, &p, &p));
    assert_eq!((plan.bytes, plan.files), (l.inv.bytes, l.inv.files));
    assert_eq!(plan.needed, df::needed_bytes(l.inv.bytes, l.inv.files, 4096));
    assert_eq!((plan.free, plan.same_volume, plan.hard_links), (Some(1 << 40), Some(false), false));
    // an empty one too
    std::fs::create_dir_all(&p).unwrap();
    assert_eq!(check(&l, &p, &all).unwrap().target, p);
    // a folder with other files: its kiln subfolder
    put(&p, "photo.jpg", b"x");
    let plan = check(&l, &p, &all).unwrap();
    assert_eq!((plan.action, &plan.target), (Action::Move, &p.join(sub())));
    // a Kiln data folder there: used as it is
    put(&p, &format!("{}/profiles/x/meta.json", sub()), b"{}");
    let plan = check(&l, &p, &all).unwrap();
    assert_eq!((plan.action, &plan.target, plan.bytes, plan.needed), (Action::Use, &p.join(sub()), 0, 0));
    // a mount point gets the subfolder, even empty
    let usb = l.base.join("usb");
    std::fs::create_dir_all(&usb).unwrap();
    assert_eq!(check(&l, &usb, &Facts { volume_root: true, ..Facts::all_clear() }).unwrap().target, usb.join(sub()));
    // `.` names and a trailing slash are dropped
    let plan = check(&l, &PathBuf::from(format!("{}/./next/", l.base.display())), &all).unwrap();
    assert_eq!(plan.target, l.base.join("next"));
}

#[test]
fn moving_back_to_the_default_root() {
    let s = scratch("back");
    let env = env_at(&s.0);
    let default = env.default_root().unwrap();
    // what the default root keeps after a finished (acknowledged) move: the pointer, lock files
    for f in [df::POINTER, "quick.lock", df::LOCK] {
        put(&default, f, b"");
    }
    let current = s.0.join("elsewhere/kiln");
    put(&current, "library/kiln/a/1.0.0+00000000/M.dll", b"m");
    put(&current, "Unknown Thing/x", b"yy");
    put(&current, "quick.lock", b"");
    let inv = df::inventory(&current, &env).unwrap();
    let i = CheckInput { current: &current, source: RootSource::Pointer, picked: &default, env: &env, inventory: &inv, installs: &[], servers: &[], locked: false };
    let plan = df::check(&i, &Facts::all_clear()).unwrap();
    assert_eq!((plan.action, &plan.target), (Action::Move, &default));
    // a move that is not finished yet: its journal is there
    put(&default, df::JOURNAL, b"{}");
    assert_eq!(code(df::check(&i, &Facts::all_clear())), RefusalCode::MovePending);
    std::fs::remove_file(default.join(df::JOURNAL)).unwrap();
    // the default root with launcher entries only (a launcher before dev.25 ran there) is refused, naming them
    put(&default, "settings.json", b"{}\n");
    put(&default, "logs/launcher.log", b"a line\n");
    let r = df::check(&i, &Facts::all_clear()).unwrap_err();
    assert_eq!(r.code, RefusalCode::DefaultRootTaken);
    assert!(r.reason.contains("logs") && r.reason.contains("settings.json") && r.reason.contains("remove them first"), "{}", r.reason);
    assert_eq!(names(&plan.unknown), ["Unknown Thing"]);
    assert_eq!((plan.bytes, plan.files), (3, 2), "the unknown entry counts in the space, the lock does not");
}

#[test]
#[cfg(unix)] // a Wine prefix whose Z: is a symlink; on Windows every game is native mode
fn a_place_that_forces_copy_mode_is_named_not_refused() {
    let l = light("copy-mode");
    let pfx = l.base.join("pfx");
    std::fs::create_dir_all(pfx.join("dosdevices")).unwrap();
    symlink("/", pfx.join("dosdevices/z:")).unwrap();
    let z = install_at(&l.base.join("game-z"), Some(pfx.clone()));
    let flatpak = GameInstall { steam_flavor: Some(kiln_manage::steam::SteamFlavor::Flatpak), ..install_at(&l.base.join("game-flatpak"), Some(pfx)) };
    let installs = [z.clone(), flatpak.clone()];
    let p = l.base.join("odd:name"); // a name Windows cannot have: no Z: path
    let mut i = input(&l, &p);
    i.installs = &installs;
    let plan = df::check(&i, &Facts::all_clear()).unwrap();
    assert_eq!(plan.copy_mode_now, [flatpak.id.clone()]);
    assert_eq!(plan.copy_mode_after, [z.id.clone(), flatpak.id.clone()]);
    let p = l.base.join("fine name");
    let mut i = input(&l, &p);
    i.installs = &installs;
    assert_eq!(df::check(&i, &Facts::all_clear()).unwrap().copy_mode_after, [flatpak.id]);
}

// ------------------------------------------------------------------------------------------------ the system's probes

#[test]
fn the_real_probes_on_one_disk() {
    let s = scratch("probes");
    let dirs = DataDirs::at(s.0.join("data"));
    let facts = RealFacts { dirs: &dirs };
    let (a, b) = (s.0.join("a"), s.0.join("b"));
    std::fs::create_dir_all(&a).unwrap();
    std::fs::create_dir_all(&b).unwrap();
    assert!(facts.writable(&a));
    #[cfg(unix)] // a folder without write permission
    {
        let ro = s.0.join("read-only");
        std::fs::create_dir_all(&ro).unwrap();
        chmod(&ro, 0o555);
        let w = facts.writable(&ro);
        chmod(&ro, 0o755);
        assert!(!w);
    }
    assert_eq!(facts.same_volume(&a, &b), Some(true));
    assert!(facts.hard_links(&a, &b), "this scratch disk has hard links");
    let sp = facts.space(&a).unwrap();
    assert!(sp.free > 0 && sp.unit > 0, "{sp:?}");
    let top = s.0.ancestors().last().unwrap(); // `/`, or the drive root `C:\`
    assert!(facts.volume_root(top), "{top:?}");
    assert!(!facts.volume_root(&a));
    assert_eq!(std::fs::read_dir(&a).unwrap().count() + std::fs::read_dir(&b).unwrap().count(), 0, "the probes leave nothing behind");
    let g = install_at(&s.0.join("game"), None);
    put(&g.path, "enshrouded.exe", b"MZ");
    assert_eq!(facts.game_running(&[g]).unwrap(), None);
}

/// Across two volumes: `KILN_TEST_VOLUME_B` names a folder on another volume (the wave's exFAT disk); skipped, and
/// said so, when it is not set or not there.
#[test]
fn the_real_probes_across_two_volumes() {
    let Some(b) = std::env::var_os("KILN_TEST_VOLUME_B").map(PathBuf::from) else {
        eprintln!("SKIPPED the_real_probes_across_two_volumes: KILN_TEST_VOLUME_B is not set");
        return;
    };
    if !b.is_dir() {
        eprintln!("SKIPPED the_real_probes_across_two_volumes: {} is not there (the disk is not mounted)", b.display());
        return;
    }
    let s = scratch("probes-b");
    let dirs = DataDirs::at(s.0.join("data"));
    let facts = RealFacts { dirs: &dirs };
    let other = b.join(format!("probes-{}", std::process::id()));
    std::fs::create_dir_all(&other).unwrap();
    let same = facts.same_volume(&s.0, &other);
    let links = facts.hard_links(&s.0, &other);
    let writable = facts.writable(&other);
    let space = facts.space(&other);
    let root = other.ancestors().find(|a| facts.volume_root(a)).map(Path::to_path_buf);
    let left = std::fs::read_dir(&other).unwrap().count();
    std::fs::remove_dir_all(&other).unwrap();
    eprintln!("volume B: same volume {same:?}, hard links {links}, writable {writable}, space {space:?}, mounted at {root:?}");
    assert_eq!(same, Some(false));
    assert!(!links, "a hard link never crosses volumes");
    assert!(writable);
    assert!(space.is_some_and(|s| s.unit > 0));
    assert!(root.is_some_and(|r| r != Path::new("/") && !s.0.starts_with(r)), "the mount point (Windows: its drive) is a volume root");
    assert_eq!(left, 0, "the probes leave nothing behind");
}

// ------------------------------------------------------------------------------------------------ recorded paths

// The list of recorded paths is taken with Linux rules (Z mode and copy mode, the Z: forms): Unix only.

/// One place where a file names the data root.
#[cfg(unix)]
#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord)]
struct Hit {
    file: String,
    field: String,
    form: &'static str,
}

/// `needle` occurs in `hay` as a whole path (what follows is not part of a name).
#[cfg(unix)]
fn occurrences(hay: &str, needle: &str) -> usize {
    hay.match_indices(needle)
        .filter(|(i, _)| hay[i + needle.len()..].chars().next().is_none_or(|c| !(c.is_alphanumeric() || "-_.".contains(c))))
        .count()
}

/// Where in a file the data root is named: JSON fields by pointer, INI lines by section, other text by line, else the
/// whole file. Every occurrence in the bytes must be found in a field, or the file is reported as `(bytes)`.
#[cfg(unix)]
fn hits_in(file: &str, bytes: &[u8], host: &str, z: &str) -> Vec<Hit> {
    let z_json = z.replace('\\', "\\\\");
    let text = String::from_utf8_lossy(bytes);
    let raw = occurrences(&text, host) + occurrences(&text, z) + occurrences(&text, &z_json);
    if raw == 0 {
        return vec![];
    }
    let mut out = Vec::new();
    let mut found = 0;
    if let Ok(v) = serde_json::from_slice::<serde_json::Value>(bytes) {
        fn walk(v: &serde_json::Value, ptr: String, f: &mut dyn FnMut(&str, &str)) {
            match v {
                serde_json::Value::String(s) => f(&ptr, s),
                serde_json::Value::Array(a) => a.iter().enumerate().for_each(|(i, x)| walk(x, format!("{ptr}/{i}"), f)),
                serde_json::Value::Object(o) => o.iter().for_each(|(k, x)| {
                    f(&format!("{ptr}/{k} (key)"), k);
                    walk(x, format!("{ptr}/{k}"), f)
                }),
                _ => {}
            }
        }
        walk(&v, String::new(), &mut |ptr, s| {
            for (needle, form) in [(host, "host"), (z, "Z: (JSON-escaped)")] {
                let n = occurrences(s, needle);
                if n > 0 {
                    found += n;
                    out.push(Hit { file: file.into(), field: ptr.into(), form });
                }
            }
        });
    } else if std::str::from_utf8(bytes).is_ok() {
        let ini = file.ends_with(".ini");
        let (mut section, mut n) = (String::new(), 0);
        for (i, line) in text.lines().enumerate() {
            let t = line.trim();
            if ini && t.starts_with('[') {
                section = t.to_string();
                n = 0;
                continue;
            }
            if !t.is_empty() && !t.starts_with(';') && !t.starts_with('#') {
                n += 1;
            }
            for (needle, form) in [(host, "host"), (z, "Z:")] {
                let k = occurrences(line, needle);
                if k > 0 {
                    found += k;
                    let field = if ini { format!("{section} line {n}") } else { format!("line {}", i + 1) };
                    out.push(Hit { file: file.into(), field, form });
                }
            }
        }
    }
    if found != raw {
        out.push(Hit { file: file.into(), field: format!("(bytes: {raw} found, {found} in fields)"), form: "?" });
    }
    out
}

#[cfg(unix)]
fn files_below(dir: &Path, out: &mut Vec<PathBuf>) {
    for e in std::fs::read_dir(dir).into_iter().flatten().flatten() {
        let Ok(m) = std::fs::symlink_metadata(e.path()) else { continue };
        if m.is_dir() {
            files_below(&e.path(), out);
        } else if m.is_file() {
            out.push(e.path());
        }
    }
}

/// Who writes and reads each record the test finds, and what a move must do with it (the table's other columns).
#[cfg(unix)]
fn about(file: &str, field: &str) -> Option<(&'static str, &'static str, &'static str)> {
    const ACTIVATE: &str = "written again by activation from the new place (`core-2` activates the active profiles again)";
    Some(match (file, field) {
        ("<game Z>/kiln/profile.json", "/configDir") => ("`profile::activate` / `refresh_active` (Z and native mode)", "kiln-core (`KILN_PATH_CONFIG`), Kiln.Host, the native loader", ACTIVATE),
        ("<game Z>/kiln/profile.json", "/mods/kiln/0/path") => ("`profile::activate` (`render`)", "kiln-core, Kiln.Host (Kiln mods)", ACTIVATE),
        ("<game Z>/kiln/profile.json", "/mods/native/0/path") => ("`profile::activate` (`render`)", "the native loader (kiln-core)", ACTIVATE),
        ("<game Z>/kiln/profile.json", "/mods/emf/0/path") => ("`profile::activate` (`render`)", "kiln-emf", ACTIVATE),
        ("<game Z>/kiln/chain.ini", "[profile] line 1") => ("`profile::activate` (`compat::plan`, `chain::write_profile_plan`): a dllmain native's DLL", "the proxy (`dinput8.dll`), before the game starts", ACTIVATE),
        ("<data>/servers/isles.json", "/backupDir") => (
            "the launcher (`server::instance::import`, `record`), from `steam_instance`: `<data>/backups/servers/<id>`",
            "`server::backup` (list, create, restore of the server's tarball backups)",
            "carried over: rewrite the old root to the new one, else backups are read from and written to the old place",
        ),
        ("<server>/kiln/data/kiln-network/modpack-build.json", "/file") => (
            "`modpack::build` (launcher `server_modpack_build`): `<data>/modpacks/built/<server>/<name>.zip`",
            "`modpack::publish_state` (the Servers page shows it; `publish` uses only its sha256, size, name and version)",
            "a record that goes stale: the zip moves with `modpacks/built/`; carry it over (rewrite the root) or build again",
        ),
        _ => return None,
    })
}

#[cfg(unix)]
const REPORT_BEGIN: &str = "<!-- data_folder test: begin (written by `cargo test --test data_folder`, KILN_TEST_REPORT) -->";
#[cfg(unix)]
const REPORT_END: &str = "<!-- data_folder test: end -->";

/// Writes the test's table between the markers of the file named by `KILN_TEST_REPORT`, keeping what is around them.
#[cfg(unix)]
fn write_report(path: &Path, hits: &BTreeSet<Hit>, searched: &BTreeMap<&'static str, usize>) {
    let mut t = String::new();
    t.push_str(REPORT_BEGIN);
    t.push_str("\n## Proven by the test\n\n");
    t.push_str("`recorded_absolute_paths_of_the_data_root` builds the fixture (`crates/kiln-manage/tests/fixture/mod.rs`), activates the \
                active profile again in both game folders, and searches every file below these folders for the data root as a host \
                path, as its `Z:\\` form and as that form with JSON-escaped backslashes:\n\n");
    for (area, n) in searched {
        t.push_str(&format!("- `{area}`: {n} {}\n", if *n == 1 { "file" } else { "files" }));
    }
    t.push_str("\nIt asserts exactly this set (a new record that names the data root fails it):\n\n");
    t.push_str("| File | Field | Form | Written by | Read by | What a move must do |\n|---|---|---|---|---|---|\n");
    for h in hits {
        let (w, r, m) = about(&h.file, &h.field).unwrap_or(("?", "?", "?"));
        t.push_str(&format!("| `{}` | `{}` | {} | {w} | {r} | {m} |\n", h.file, h.field, h.form));
    }
    t.push_str("\nNo other file of the fixture names it: not the install records (`installs/<id>.json`, `<id>.active.json`), the \
                library's import records, the profiles, the save backup's `backup.json` (its sources are the save folder), the \
                runtime bundle, the copy-mode game's `profile.json` and `chain.ini` (relative paths), the placement receipt \
                (`kiln/state/library-placements.json`) and `.kiln-library.json` markers, `settings.json`, `nexus/handler.json` \
                (it names the launcher), nor the state and settings folders (the fixture's and, under the run rules, the process's \
                `XDG_STATE_HOME`/`XDG_CONFIG_HOME`; the crate writes nothing there in these flows).\n");
    t.push_str(REPORT_END);
    let old = std::fs::read_to_string(path).unwrap_or_default();
    let new = match (old.find(REPORT_BEGIN), old.find(REPORT_END)) {
        (Some(a), Some(b)) if b > a => format!("{}{t}{}", &old[..a], &old[b + REPORT_END.len()..]),
        _ => format!("# Absolute paths that name the launcher's data folder\n\n{t}\n\n## Read from the code\n\n(by hand)\n"),
    };
    std::fs::write(path, new).unwrap();
}

/// Every file the fixture made that names the data root, and which field: the exact list, so a new record fails here.
#[test]
#[cfg(unix)] // Z mode and copy mode, and the Z: forms of the root: Linux rules
fn recorded_absolute_paths_of_the_data_root() {
    let fx = Fixture::build("recorded");
    let (z, copy) = fx.activate();
    assert_eq!((z.path_mode, copy.path_mode), (PathMode::Z, PathMode::Copy));
    let host = fx.dirs.root.to_string_lossy().into_owned();
    let zf = kiln_manage::winpath::unix_to_z(&fx.dirs.root).unwrap();
    let mut areas = fx.areas();
    // the process's own state and settings folders, where the run rules put them on scratch
    if std::env::var_os("XDG_STATE_HOME").is_some() && std::env::var_os("XDG_CONFIG_HOME").is_some() {
        areas.push(("<XDG_STATE_HOME>/kiln", kiln_manage::util::state_dir()));
        areas.push(("<XDG_CONFIG_HOME>/kiln", kiln_manage::util::config_dir()));
    }
    let mut files = Vec::new();
    files_below(&fx.base, &mut files);
    for (_, dir) in areas.iter().filter(|(_, d)| !d.starts_with(&fx.base)) {
        files_below(dir, &mut files);
    }
    let mut hits = BTreeSet::new();
    let mut searched: BTreeMap<&'static str, usize> = areas.iter().map(|(l, _)| (*l, 0)).collect();
    for f in &files {
        // the most specific area the file is in
        let (label, dir) = areas.iter().filter(|(_, d)| f.starts_with(d)).max_by_key(|(_, d)| d.components().count()).unwrap();
        *searched.entry(*label).or_default() += 1;
        let rel = format!("{label}/{}", f.strip_prefix(dir).unwrap().to_string_lossy());
        hits.extend(hits_in(&rel, &std::fs::read(f).unwrap(), &host, &zf));
    }
    let expected: BTreeSet<Hit> = [
        ("<game Z>/kiln/profile.json", "/configDir", "Z: (JSON-escaped)"),
        ("<game Z>/kiln/profile.json", "/mods/kiln/0/path", "Z: (JSON-escaped)"),
        ("<game Z>/kiln/profile.json", "/mods/native/0/path", "Z: (JSON-escaped)"),
        ("<game Z>/kiln/profile.json", "/mods/emf/0/path", "Z: (JSON-escaped)"),
        ("<game Z>/kiln/chain.ini", "[profile] line 1", "Z:"),
        ("<data>/servers/isles.json", "/backupDir", "host"),
        ("<server>/kiln/data/kiln-network/modpack-build.json", "/file", "host"),
    ]
    .into_iter()
    .map(|(file, field, form)| Hit { file: file.into(), field: field.into(), form })
    .collect();
    let got: Vec<_> = hits.iter().map(|h| (h.file.as_str(), h.field.as_str(), h.form)).collect();
    assert_eq!(hits, expected, "the files that name the data root changed: {got:#?}");
    assert!(searched.get("<data>").is_some_and(|n| *n > 20) && searched.contains_key("<game copy>"), "{searched:?}");
    for h in &hits {
        assert!(about(&h.file, &h.field).is_some(), "describe {h:?} in about()");
    }
    if let Some(p) = std::env::var_os("KILN_TEST_REPORT") {
        write_report(Path::new(&p), &hits, &searched);
    }
}

/// The fixture's data folder is a Kiln data folder by its layout, and checking a move of it works end to end with the
/// real probes (this scratch disk).
#[test]
fn the_fixture_is_a_kiln_data_folder_and_can_be_checked_with_the_real_probes() {
    let fx = Fixture::build("real-check");
    assert_eq!(df::recognize(&fx.dirs.root, &fx.env).unwrap(), FolderState::Kiln { by: Evidence::Library });
    let inv = df::inventory(&fx.dirs.root, &fx.env).unwrap();
    let installs = kiln_manage::game::known(&fx.dirs).unwrap();
    assert_eq!(installs.len(), 2);
    let servers = [fx.server.clone()];
    let p = fx.base.join("moved");
    let i = CheckInput { current: &fx.dirs.root, source: RootSource::Default, picked: &p, env: &fx.env, inventory: &inv, installs: &installs, servers: &servers, locked: false };
    let plan = df::check(&i, &RealFacts { dirs: &fx.dirs }).unwrap();
    let private = if cfg!(unix) { Some(true) } else { None };
    assert_eq!((plan.action, plan.same_volume, plan.hard_links, plan.private_files), (Action::Move, Some(true), true, private));
    assert!(inv.in_place_files > 0, "the active profile's config is written in place");
    let unit = RealFacts { dirs: &fx.dirs }.space(&fx.base).unwrap().unit;
    assert_eq!(plan.needed, df::needed_bytes(inv.in_place_bytes, inv.in_place_files, unit));
    assert_eq!((plan.bytes, plan.files), (inv.bytes, inv.files));
    assert_eq!(names(&plan.unknown), ["My Notes"]);
    let copy: Vec<String> = if cfg!(windows) { vec![] } else { vec![fx.game_copy.id.clone()] }; // Windows: native mode
    assert_eq!((plan.copy_mode_now.clone(), plan.copy_mode_after.clone()), (copy.clone(), copy));
    // the game folders and the server's are refused as places
    for inside in [fx.game_z.path.join("kiln/data-here"), fx.server.join("x")] {
        let i = CheckInput { picked: &inside, ..CheckInput { current: &fx.dirs.root, source: RootSource::Default, picked: &p, env: &fx.env, inventory: &inv, installs: &installs, servers: &servers, locked: false } };
        let c = df::check(&i, &RealFacts { dirs: &fx.dirs }).unwrap_err().code;
        assert!(matches!(c, RefusalCode::InsideGame | RefusalCode::InsideServer), "{inside:?}: {c:?}");
    }
}
