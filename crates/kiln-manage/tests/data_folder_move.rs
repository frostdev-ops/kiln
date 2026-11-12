//! Moving the data folder (dev.25 `data-folder`, task core-2): locating through the pointer and every broken pointer,
//! `reset` and `use`, the lock, moves by hard links, by copies and across two volumes, failures and cancels at each
//! step with the old place intact and in use, a crash at each step and the recovery a new process runs, removal only
//! with proof, writes through the new place leaving a hard-linked old copy as it was, the records that name the root
//! after a move, and another data folder's profile in a game (Z mode and copy mode, another id and the same id).
//!
//! The file builds for Windows too (run under Wine, see tests/data_folder_windows.rs): what needs a symlink, Unix
//! permissions, inode numbers or file modes, or Z mode and copy mode (on Windows every game is native mode) is Unix
//! only, each marked where it is; on Windows the fixture is a Windows player's (`fixture::win_env_at`).
#![cfg(feature = "zip")]

mod fixture;

use fixture::{Fixture, env_at, kiln_mod, put, scratch};
use kiln_manage::data_folder::{self as df, DataLock, Facts, FolderState, RealFacts};
use kiln_manage::data_folder_move::{self as dfm, Inject, MoveOptions, Pending, Step};
#[cfg(unix)]
use kiln_manage::game::PathMode;
use kiln_manage::paths::{DataDirs, DataEnv, ProblemKind, RootSource};
use kiln_manage::profile::{self, GameProfileOwner};
use kiln_manage::task::{NoProgress, Progress};
use kiln_manage::{ErrorCode, library, util};
#[cfg(unix)]
use kiln_manage::quick;
use std::collections::{BTreeMap, BTreeSet};
#[cfg(unix)]
use std::os::unix::fs::{MetadataExt, PermissionsExt, symlink};
use std::path::{Path, PathBuf};

/// Every regular file below `root` (not the pointer, journal, records, markers, locks): relative path -> sha256.
fn tree(root: &Path) -> BTreeMap<String, String> {
    fn go(dir: &Path, rel: &str, out: &mut BTreeMap<String, String>) {
        let Ok(rd) = std::fs::read_dir(dir) else { return };
        for e in rd.flatten() {
            let name = e.file_name().to_string_lossy().into_owned();
            let r = if rel.is_empty() { name.clone() } else { format!("{rel}/{name}") };
            let m = std::fs::symlink_metadata(e.path()).unwrap();
            let top = rel.is_empty() && (df::is_own_file(&name, style()) || df::is_top_lock(&name, style()));
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

fn style() -> df::PathStyle {
    if cfg!(windows) { df::PathStyle::Windows } else { df::PathStyle::Unix }
}

fn located(env: &DataEnv) -> kiln_manage::paths::Located {
    env.locate().unwrap()
}

/// Same volume, hard links work (this scratch disk), nothing runs.
fn linking() -> Facts {
    Facts { same_volume: Some(true), hard_links: true, ..Facts::all_clear() }
}

fn mv(fx: &Fixture, to: &Path, facts: &Facts, opts: &MoveOptions) -> kiln_manage::Result<dfm::MoveResult> {
    dfm::move_to(&located(&fx.env), &fx.env, to, facts, None, opts, &mut NoProgress)
}

fn inject(i: Inject) -> MoveOptions {
    MoveOptions { force_copy: false, inject: Some(i) }
}

/// A library entry's folder (also when it is gone).
fn lib_dir(dirs: &DataDirs, r: &library::LibraryRef) -> PathBuf {
    dirs.library_entry(r.kind, &r.id, &r.version, &r.hash).unwrap()
}

#[cfg(unix)]
fn ino(p: &Path) -> u64 {
    std::fs::metadata(p).unwrap().ino()
}

/// The old place is as it was and in use, nothing of the move is left: no pointer, no journal, no target.
fn old_place_intact(fx: &Fixture, before: &BTreeMap<String, String>, to: &Path) {
    assert_eq!(&tree(&fx.dirs.root), before, "the old place is byte for byte as before");
    assert_eq!(located(&fx.env).dirs.root, fx.dirs.root, "and in use");
    assert!(!fx.dirs.root.join(df::POINTER).exists());
    assert!(!fx.dirs.root.join(df::JOURNAL).exists() && !fx.dirs.root.join(df::FILE_RECORD).exists());
    assert!(!to.exists(), "the partial copy is gone: {:?}", std::fs::read_dir(to).map(|d| d.flatten().map(|e| e.file_name()).collect::<Vec<_>>()));
    assert_eq!(dfm::pending(&fx.env).unwrap(), Pending::Nothing);
}

// ------------------------------------------------------------------------------------------------ locating

#[test]
fn locate_order_is_env_then_pointer_then_default() {
    let s = scratch("locate-order");
    let env = env_at(&s.0);
    let default = env.default_root().unwrap();
    std::fs::create_dir_all(&default).unwrap();
    let l = env.locate().unwrap();
    assert_eq!((l.dirs.root.clone(), l.source, l.default_root.clone()), (default.clone(), RootSource::Default, Some(default.clone())));
    let elsewhere = s.0.join("elsewhere");
    put(&elsewhere, "installs/x.json", b"{}");
    df::write_pointer(&default, &elsewhere).unwrap();
    let l = env.locate().unwrap();
    assert_eq!((l.dirs.root.clone(), l.source), (elsewhere.clone(), RootSource::Pointer));
    let p: serde_json::Value = serde_json::from_slice(&std::fs::read(default.join(df::POINTER)).unwrap()).unwrap();
    assert_eq!((p["format"].as_u64(), p["path"].as_str()), (Some(1), elsewhere.to_str()));
    // KILN_DATA_DIR wins over the pointer
    let with_env = DataEnv { kiln_data_dir: Some(s.0.join("env").into()), ..env.clone() };
    let l = with_env.locate().unwrap();
    assert_eq!((l.dirs.root, l.source, l.default_root), (s.0.join("env"), RootSource::Env, Some(default.clone())));
    // a folder with the marker only is a Kiln data folder too (a move made it)
    let marked = s.0.join("marked");
    put(&marked, df::MARKER, b"{\"format\":1,\"created\":\"2026-11-11T00:00:00Z\"}");
    df::write_pointer(&default, &marked).unwrap();
    assert_eq!(env.locate().unwrap().dirs.root, marked);
    df::remove_pointer(&default).unwrap();
    assert_eq!(env.locate().unwrap().source, RootSource::Default);
}

#[test]
fn pointer_replacement_and_removal_preserve_unrelated_files() {
    let s = scratch("pointer-replace");
    let env = env_at(&s.0);
    let default = env.default_root().unwrap();
    put(&default, df::POINTER_OLD, b"the previous recovery pointer");
    put(&default, "data-folder.json.foreign.removed", b"not made by this move");
    df::write_pointer(&default, &s.0.join("first")).unwrap();
    df::write_pointer(&default, &s.0.join("second")).unwrap();
    assert_eq!(df::read_pointer(&default).unwrap().unwrap().path, s.0.join("second"));
    df::remove_pointer(&default).unwrap();
    df::remove_pointer(&default).unwrap();
    assert!(df::read_pointer(&default).unwrap().is_none());
    assert_eq!(std::fs::read(default.join(df::POINTER_OLD)).unwrap(), b"the previous recovery pointer");
    assert_eq!(std::fs::read(default.join("data-folder.json.foreign.removed")).unwrap(), b"not made by this move");
    assert_eq!(std::fs::read_dir(&default).unwrap().count(), 2, "no temporary pointer or tombstone remains");
}

#[test]
fn a_broken_pointer_is_an_error_never_the_default() {
    let s = scratch("broken");
    let env = env_at(&s.0);
    let default = env.default_root().unwrap();
    std::fs::create_dir_all(&default).unwrap();
    let pointer = default.join(df::POINTER);
    let kind = |env: &DataEnv| {
        let e = env.locate().unwrap_err();
        assert_eq!(e.code, ErrorCode::DataFolder, "{e:?}");
        let p = env.locate_checked().unwrap_err();
        assert_eq!(p.pointer.as_deref(), Some(pointer.as_path()));
        assert!(!p.reason.is_empty());
        p.kind
    };
    std::fs::write(&pointer, b"{ not json").unwrap();
    assert_eq!(kind(&env), ProblemKind::Unparsable);
    std::fs::write(&pointer, br#"{"format":1}"#).unwrap();
    assert_eq!(kind(&env), ProblemKind::Unparsable, "no path");
    std::fs::write(&pointer, br#"{"format":2,"path":"/x","written":"","writer":""}"#).unwrap();
    assert_eq!(kind(&env), ProblemKind::NewerFormat);
    std::fs::write(&pointer, br#"{"format":1,"path":"relative/kiln","written":"","writer":""}"#).unwrap();
    assert_eq!(kind(&env), ProblemKind::NotAbsolute);
    df::write_pointer(&default, &s.0.join("gone")).unwrap();
    assert_eq!(kind(&env), ProblemKind::Missing);
    let p = env.locate_checked().unwrap_err();
    assert_eq!(p.path.as_deref(), Some(s.0.join("gone").as_path()), "the problem names the folder");
    put(&s.0, "a-file", b"x");
    df::write_pointer(&default, &s.0.join("a-file")).unwrap();
    assert_eq!(kind(&env), ProblemKind::NotAFolder);
    let real = s.0.join("real");
    put(&real, "installs/x.json", b"{}");
    #[cfg(unix)] // Windows: a junction, tests/data_folder_windows.rs
    {
        symlink(&real, s.0.join("link")).unwrap();
        df::write_pointer(&default, &s.0.join("link")).unwrap();
        assert_eq!(kind(&env), ProblemKind::NotAFolder, "a data root is never a link");
    }
    std::fs::create_dir_all(s.0.join("empty")).unwrap();
    df::write_pointer(&default, &s.0.join("empty")).unwrap();
    assert_eq!(kind(&env), ProblemKind::NotKiln, "never an empty folder");
    let photos = s.0.join("photos");
    put(&photos, "a.jpg", b"x");
    df::write_pointer(&default, &photos).unwrap();
    assert_eq!(kind(&env), ProblemKind::NotKiln);
    #[cfg(unix)] // Unix permissions; Windows: a pointer opened without sharing, tests/data_folder_windows.rs
    {
        let locked = s.0.join("locked");
        put(&locked, "installs/x.json", b"{}");
        std::fs::set_permissions(&locked, std::fs::Permissions::from_mode(0o000)).unwrap();
        df::write_pointer(&default, &locked).unwrap();
        let k = kind(&env);
        std::fs::set_permissions(&locked, std::fs::Permissions::from_mode(0o755)).unwrap();
        assert_eq!(k, ProblemKind::FolderUnreadable);
        std::fs::set_permissions(&pointer, std::fs::Permissions::from_mode(0o000)).unwrap();
        let k = kind(&env);
        std::fs::set_permissions(&pointer, std::fs::Permissions::from_mode(0o644)).unwrap();
        assert_eq!(k, ProblemKind::Unreadable);
    }
    // reset: only for a broken pointer; renames it and the default root is used
    df::write_pointer(&default, &photos).unwrap();
    let l = dfm::reset(&env).unwrap();
    assert_eq!((l.dirs.root, l.source), (default.clone(), RootSource::Default));
    assert!(!pointer.exists() && default.join(df::POINTER_OLD).is_file());
    df::write_pointer(&default, &real).unwrap();
    assert_eq!(dfm::reset(&env).unwrap_err().code, ErrorCode::Refused, "a pointer that works is not reset");
    assert!(pointer.is_file());
}

#[test]
fn use_an_existing_kiln_data_folder() {
    let fx = Fixture::build("use");
    let other = fx.base.join("other kiln");
    put(&other, "profiles/elsewhere/meta.json", b"{}");
    put(&other, "settings.json", b"{}\n");
    let before = (tree(&fx.dirs.root), tree(&other));
    let l = located(&fx.env);
    let now = dfm::use_folder(&l, &fx.env, &other, &Facts::all_clear(), None).unwrap();
    assert_eq!((now.dirs.root.clone(), now.source), (other.clone(), RootSource::Pointer));
    assert_eq!((tree(&fx.dirs.root), tree(&other)), before, "nothing copied, nothing removed");
    // an empty folder is not "use this one"
    let empty = fx.base.join("empty");
    std::fs::create_dir_all(&empty).unwrap();
    let e = dfm::use_folder(&now, &fx.env, &empty, &Facts::all_clear(), None).unwrap_err();
    assert_eq!(e.detail.as_ref().and_then(|d| d["refusal"].as_str()), Some("not_a_kiln_folder"), "{e:?}");
    // and back to the default root: the pointer goes
    let back = dfm::use_folder(&now, &fx.env, &fx.dirs.root, &Facts::all_clear(), None).unwrap();
    assert_eq!((back.dirs.root, back.source), (fx.dirs.root.clone(), RootSource::Default));
    assert!(!fx.dirs.root.join(df::POINTER).exists());
}

#[test]
fn the_lock_is_shared_by_launchers_and_exclusive_for_a_move() {
    let fx = Fixture::build("lock");
    let root = &fx.dirs.root;
    let a = DataLock::shared(root).unwrap().unwrap();
    let mut b = DataLock::shared(root).unwrap().unwrap();
    assert!(DataLock::exclusive(root).unwrap().is_none(), "two launchers run: no move");
    assert!(!b.upgrade().unwrap(), "the other launcher still holds it");
    assert!(!b.is_exclusive());
    // a move while another launcher runs is refused, by the core-1 refusal
    let e = dfm::move_to(&located(&fx.env), &fx.env, &fx.base.join("moved"), &linking(), Some(&mut b), &MoveOptions::default(), &mut NoProgress).unwrap_err();
    assert_eq!(e.detail.as_ref().and_then(|d| d["refusal"].as_str()), Some("locked"), "{e:?}");
    let e = mv(&fx, &fx.base.join("moved"), &linking(), &MoveOptions::default()).unwrap_err();
    assert_eq!(e.detail.as_ref().and_then(|d| d["refusal"].as_str()), Some("locked"));
    drop(a);
    assert!(b.upgrade().unwrap() && b.is_exclusive());
    assert!(DataLock::shared(root).unwrap().is_none(), "a launcher started during a move waits");
    b.downgrade().unwrap();
    assert!(DataLock::shared(root).unwrap().is_some());
    drop(b);
    // the quick flow's lock held: the launcher is busy
    let q = std::fs::OpenOptions::new().read(true).write(true).open(root.join("quick.lock")).unwrap();
    q.lock().unwrap();
    let e = mv(&fx, &fx.base.join("moved"), &linking(), &MoveOptions::default()).unwrap_err();
    assert_eq!(e.detail.as_ref().and_then(|d| d["refusal"].as_str()), Some("busy"));
    drop(q);
    assert!(!fx.base.join("moved").exists());
    assert!(!root.join(df::JOURNAL).exists());
}

// ------------------------------------------------------------------------------------------------ moves

/// A Progress that cancels once the switch is under way (the move's own removal of the old copy is then interrupted),
/// and notes whether a launcher could take the new place's lock while the move writes the games' profiles.
struct AfterSwitch {
    to: PathBuf,
    switched: bool,
    lock_free_during: Option<bool>,
}
impl Progress for AfterSwitch {
    fn report(&mut self, _: Option<f32>, m: &str) {
        if m.starts_with("switching") {
            self.switched = true;
        }
        if m.starts_with("writing the games' profiles") {
            self.lock_free_during = Some(DataLock::shared(&self.to).unwrap().is_some());
        }
    }
    fn canceled(&self) -> bool {
        self.switched
    }
}

/// A move by hard links within one volume whose own removal of the old copy is interrupted (a cancel after the switch),
/// then what the launcher writes in normal use, through the new place: the old copy stays byte for byte as it was.
/// Then the old copy is removed by the record at the next start and the move acknowledged.
#[test]
fn a_move_by_hard_links_leaves_the_old_copy_untouched_by_later_writes() {
    let fx = Fixture::build("links");
    let before = tree(&fx.dirs.root);
    let to = fx.base.join("moved");
    let mut p = AfterSwitch { to: to.clone(), switched: false, lock_free_during: None };
    let r = dfm::move_to(&located(&fx.env), &fx.env, &to, &linking(), None, &MoveOptions::default(), &mut p).unwrap();
    let c = r.cleanup.clone().unwrap();
    assert!(c.interrupted && c.removed_files == 0, "a cancel after the switch interrupts only the removal: {c:?}");
    assert_eq!(p.lock_free_during, Some(false), "a launcher starting meanwhile cannot take the new place");
    assert!(DataLock::shared(&to).unwrap().is_some(), "after the move it can");
    assert_eq!((r.from.clone(), r.to.clone()), (fx.dirs.root.clone(), to.clone()));
    assert!(r.linked > 0 && r.copied > 0, "{r:?}");
    assert_eq!(r.files as usize, before.len());
    let (old_t, new_t) = (tree(&fx.dirs.root), tree(&to));
    assert_eq!(old_t.keys().collect::<Vec<_>>(), new_t.keys().collect::<Vec<_>>(), "every file is in the new place");
    for (k, v) in old_t.iter().filter(|(k, _)| k.starts_with("library/") || k.starts_with("runtime/") || k.starts_with("modpacks/")) {
        assert_eq!(&new_t[k], v, "{k}");
    }
    assert_eq!(r.stayed.iter().map(|s| (s.name.as_str(), s.path.clone())).collect::<Vec<_>>(), [("My Notes", fx.dirs.root.join("My Notes"))]);
    let lib = lib_dir(&fx.dirs, &fx.mods.kiln).join("M.dll");
    let new_lib = lib_dir(&DataDirs::at(&to), &fx.mods.kiln).join("M.dll");
    #[cfg(unix)] // inode numbers (Windows: the record's `linked` says it, tests/data_folder_windows.rs)
    {
        assert_eq!(ino(&lib), ino(&new_lib), "library files are hard-linked");
        let cfg = |root: &Path| root.join(format!("profiles/{}/config/native.json", fx.active));
        assert_ne!(ino(&cfg(&fx.dirs.root)), ino(&cfg(&to)), "a profile's config is written in place: copied for real");
    }
    let l = located(&fx.env);
    assert_eq!((l.dirs.root.clone(), l.source), (to.clone(), RootSource::Pointer));
    assert_eq!(r.activated.len(), 2, "both games' profiles written again from the new place");
    // what the launcher writes in normal use, through the new place
    let new = DataDirs::at(&to);
    util::write_json_atomic(&new.settings(), &serde_json::json!({ "steamRoots": [], "savesKeep": 3 })).unwrap();
    let mut p = profile::get(&new, &fx.spare).unwrap();
    p.name = "Spare, renamed".into();
    profile::save(&new, &p).unwrap();
    kiln_manage::game::write_record(&new, &fx.game_z, None).unwrap();
    library::import(&new, &[kiln_mod(&fx.base, "after-move", "1.0.0")], &mut NoProgress).unwrap();
    profile::activate(&new, &fx.active, &fx.game_z).unwrap();
    profile::activate(&new, &fx.active, &fx.game_copy).unwrap();
    put(&fx.game_copy.path, &format!("kiln/profiles/{}/config/new-items.json", fx.active), b"{\"written\":\"by the game\"}");
    profile::copy_back(&new, &fx.active, &fx.game_copy).unwrap();
    assert_eq!(tree(&fx.dirs.root), before, "the old copy is byte for byte as it was before the move");
    // the old copy goes by the record; what changed in the new place since keeps its old copy
    let c = dfm::remove_old_copy(&fx.env, &mut NoProgress).unwrap();
    assert!(c.done && !c.interrupted, "{c:?}");
    let again = dfm::remove_old_copy(&fx.env, &mut NoProgress).unwrap();
    assert!(again.done && again.removed_files == c.removed_files, "a call again with nothing left: {again:?}");
    let kept: BTreeSet<PathBuf> = c.kept.iter().map(|k| k.path.clone()).collect();
    assert!(kept.contains(&fx.dirs.root.join("My Notes")), "an unknown entry stays: {kept:?}");
    assert!(kept.contains(&fx.dirs.root.join("settings.json")), "changed in the new place since: the old one stays");
    assert!(!lib.exists(), "a library file identical in the new place is gone");
    assert!(new_lib.is_file());
    assert!(fx.dirs.root.join("My Notes/readme.txt").is_file());
    assert!(fx.dirs.root.join(df::POINTER).is_file(), "the default root keeps its pointer");
    assert!(!c.folder_removed, "the default root is never removed");
    let j = dfm::read_journal(&fx.env).unwrap().unwrap();
    assert_eq!((j.state.as_str(), j.result.as_ref().map(|r| r.done)), ("cleaned", Some(true)));
    dfm::acknowledge(&fx.env).unwrap();
    assert!(!fx.dirs.root.join(df::JOURNAL).exists() && !fx.dirs.root.join(df::FILE_RECORD).exists());
    assert!(!to.join(df::MOVE_MARKER).exists() && to.join(df::MARKER).is_file());
    assert_eq!(df::recognize(&to, &fx.env).unwrap(), FolderState::Kiln { by: df::Evidence::Marker });
    assert_eq!(located(&fx.env).dirs.root, to);
}

/// A move by real copies on one volume: times and permissions kept, `nexus/` private (Unix modes).
#[test]
fn a_move_by_copies_keeps_times_and_permissions() {
    let fx = Fixture::build("copies");
    let to = fx.base.join("copied");
    let old_handler = fx.dirs.root.join("nexus/handler.json");
    let mtime = std::fs::metadata(&old_handler).unwrap().modified().unwrap();
    #[cfg(unix)]
    let (old_ino, nexus_mode) = (ino(&old_handler), std::fs::metadata(fx.dirs.root.join("nexus")).unwrap().permissions().mode() & 0o777);
    let r = mv(&fx, &to, &linking(), &MoveOptions { force_copy: true, inject: None }).unwrap();
    assert_eq!(r.linked, 0);
    let h = to.join("nexus/handler.json");
    #[cfg(unix)]
    {
        assert_ne!(ino(&h), old_ino);
        assert_eq!(std::fs::metadata(&h).unwrap().permissions().mode() & 0o777, 0o600);
        assert_eq!(std::fs::metadata(to.join("nexus")).unwrap().permissions().mode() & 0o777, nexus_mode);
    }
    assert_eq!(std::fs::metadata(&h).unwrap().modified().unwrap(), mtime);
    assert_eq!(located(&fx.env).dirs.root, to);
}

/// To a second volume (the exFAT disk) and back to the default root. Skipped, and said so, without
/// `KILN_TEST_VOLUME_B` or when the disk is not there.
#[cfg(unix)]
#[test]
fn a_symlink_alias_of_an_inside_folder_is_refused_before_creation() {
    let fx = Fixture::build("inside-symlink");
    let alias = fx.base.join("alias");
    symlink(fx.dirs.root.join("library"), &alias).unwrap();
    let picked = alias.join("new-destination");
    let before = tree(&fx.dirs.root);
    let result = df::check_here(&located(&fx.env), &fx.env, &picked, false).unwrap();
    assert!(matches!(result, Err(ref r) if r.code == df::RefusalCode::InsideCurrent), "{result:?}");
    assert!(!picked.exists());
    assert_eq!(tree(&fx.dirs.root), before);
}

#[cfg(unix)]
#[test]
fn an_inside_target_with_another_case_is_refused_by_identity() {
    let Some(b) = std::env::var_os("KILN_TEST_VOLUME_B").map(PathBuf::from).filter(|b| b.is_dir()) else {
        eprintln!("SKIPPED identity alias: no second volume"); return;
    };
    struct Remove(PathBuf);
    impl Drop for Remove { fn drop(&mut self) { let _ = std::fs::remove_dir_all(&self.0); } }
    let base = b.join(format!("sol-f6-{}", std::process::id()));
    std::fs::create_dir(&base).unwrap();
    let _remove = Remove(base.clone());
    let fx = Fixture::build("identity-alias");
    let to = base.join("CURRENT");
    mv(&fx, &to, &Facts { same_volume: Some(false), ..Facts::all_clear() }, &MoveOptions::default()).unwrap();
    dfm::acknowledge(&fx.env).unwrap();
    let alias = base.join("current");
    if !alias.is_dir() { eprintln!("SKIPPED identity alias: second volume is case-sensitive"); return; }
    assert_eq!(std::fs::metadata(&alias).unwrap().ino(), std::fs::metadata(&to).unwrap().ino());
    let picked = alias.join("backups/new-destination");
    let before = tree(&to);
    let result = df::check_here(&located(&fx.env), &fx.env, &picked, false).unwrap();
    assert!(matches!(result, Err(ref r) if r.code == df::RefusalCode::InsideCurrent), "{result:?}");
    assert!(!picked.exists(), "refuse before making the target");
    assert_eq!(tree(&to), before);
}

#[test]
fn a_move_across_two_volumes_and_back() {
    let Some(b) = std::env::var_os("KILN_TEST_VOLUME_B").map(PathBuf::from) else {
        eprintln!("SKIPPED a_move_across_two_volumes_and_back: KILN_TEST_VOLUME_B is not set");
        return;
    };
    if !b.is_dir() {
        eprintln!("SKIPPED a_move_across_two_volumes_and_back: {} is not there (the disk is not mounted)", b.display());
        return;
    }
    let fx = Fixture::build("volumes");
    let before = tree(&fx.dirs.root);
    let to = b.join(format!("moved-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&to);
    let facts = RealFacts { dirs: &fx.dirs };
    let plan = df::check_here(&located(&fx.env), &fx.env, &to, false).unwrap().unwrap();
    let private = if cfg!(unix) { Some(false) } else { None };
    assert_eq!((plan.same_volume, plan.hard_links, plan.private_files), (Some(false), false, private), "{plan:?}");
    let r = dfm::move_to(&located(&fx.env), &fx.env, &to, &facts, None, &MoveOptions::default(), &mut NoProgress).unwrap();
    assert_eq!(r.linked, 0);
    assert_eq!(tree(&to).len(), before.len());
    #[cfg(unix)]
    {
        let mode = std::fs::metadata(to.join("nexus/handler.json")).unwrap().permissions().mode() & 0o777;
        eprintln!("on the second volume nexus/handler.json has mode {mode:o} (written 0600)");
    }
    assert_eq!(located(&fx.env).dirs.root, to);
    let c = dfm::remove_old_copy(&fx.env, &mut NoProgress).unwrap();
    assert!(c.done, "{c:?}");
    dfm::acknowledge(&fx.env).unwrap();
    // and back to the first volume
    let back_to = fx.base.join("back");
    let r = dfm::move_to(&located(&fx.env), &fx.env, &back_to, &facts, None, &MoveOptions::default(), &mut NoProgress).unwrap();
    assert_eq!((r.to.clone(), r.linked), (back_to.clone(), 0));
    assert_eq!(located(&fx.env).dirs.root, back_to);
    let c = dfm::remove_old_copy(&fx.env, &mut NoProgress).unwrap();
    assert!(c.done && c.kept.iter().all(|k| k.path.starts_with(&to)), "{c:?}");
    assert_eq!(c.kept.iter().map(|k| k.path.clone()).collect::<Vec<_>>(), [to.join("My Notes")], "only the unknown entry stays on the second volume");
    assert!(!c.folder_removed);
    dfm::acknowledge(&fx.env).unwrap();
    let left: Vec<String> = c.kept.iter().map(|k| format!("{} ({})", k.path.display(), k.reason)).collect();
    let _ = std::fs::remove_dir_all(&to);
    eprintln!("left on the second volume after the move back: {left:#?}");
    let mut now = tree(&back_to);
    let mut was = before.clone();
    for t in [&mut now, &mut was] {
        t.retain(|k, _| !k.starts_with("installs/") && !k.starts_with("servers/") && !k.contains("/config/"));
    }
    assert_eq!(now, was, "the data came back as it was (the records that name the root are written again)");
}

/// Moving back to the default root: once the old copy is removed, the default root holds only its pointer and is the
/// target; the move removes the pointer (the commit).
#[test]
fn moving_back_to_the_default_root() {
    let s = scratch("back-default");
    let env = env_at(&s.0);
    let default = env.default_root().unwrap();
    put(&default, "library/kiln/a/1.0.0+00000000/M.dll", b"mod");
    put(&default, "settings.json", b"{}\n");
    let before = tree(&default);
    let away = s.0.join("away");
    let r = dfm::move_to(&env.locate().unwrap(), &env, &away, &linking(), None, &MoveOptions::default(), &mut NoProgress).unwrap();
    let c = r.cleanup.unwrap();
    assert!(c.done && c.kept.is_empty(), "{c:?}");
    assert!(dfm::remove_old_copy(&env, &mut NoProgress).unwrap().done);
    dfm::acknowledge(&env).unwrap();
    assert_eq!(df::recognize(&default, &env).unwrap(), FolderState::Empty, "only the pointer is left");
    let r = dfm::move_to(&env.locate().unwrap(), &env, &default, &linking(), None, &MoveOptions::default(), &mut NoProgress).unwrap();
    assert_eq!(r.to, default);
    let l = env.locate().unwrap();
    assert_eq!((l.dirs.root.clone(), l.source), (default.clone(), RootSource::Default));
    assert!(!default.join(df::POINTER).exists(), "the commit removed the pointer");
    let c = dfm::remove_old_copy(&env, &mut NoProgress).unwrap();
    assert!(c.done && c.folder_removed, "{c:?}");
    dfm::acknowledge(&env).unwrap();
    assert_eq!(tree(&default), before);
    assert!(!away.exists());
}

// ------------------------------------------------------------------------------------------------ failures and crashes

#[test]
fn a_failure_after_the_copy_the_verify_or_the_switch_leaves_the_old_place_in_use() {
    for i in [Inject::FailAfterCopy, Inject::FailAfterVerify, Inject::FailCheckAfterCommit, Inject::FullAfter(1)] {
        let fx = Fixture::build(&format!("fail-{i:?}").replace(['(', ')'], ""));
        let before = tree(&fx.dirs.root);
        let to = fx.base.join("target");
        for facts in [linking(), Facts::all_clear()] {
            let e = mv(&fx, &to, &facts, &inject(i)).unwrap_err();
            assert_ne!(e.code, ErrorCode::Internal, "{i:?}: {e:?}");
            old_place_intact(&fx, &before, &to);
        }
    }
}

/// A Progress that cancels after `n` looks.
struct CancelAfter(std::cell::Cell<u32>);
impl Progress for CancelAfter {
    fn report(&mut self, _: Option<f32>, _: &str) {}
    fn canceled(&self) -> bool {
        let n = self.0.get();
        self.0.set(n.saturating_sub(1));
        n == 0
    }
}

#[test]
fn a_cancel_at_any_point_before_the_switch_leaves_the_old_place_in_use() {
    let fx = Fixture::build("cancel");
    let before = tree(&fx.dirs.root);
    let to = fx.base.join("target");
    let e = mv(&fx, &to, &linking(), &inject(Inject::CancelAfter(5))).unwrap_err();
    assert_eq!(e.code, ErrorCode::Canceled);
    old_place_intact(&fx, &before, &to);
    // a cancel after 0, 4, 8, ... looks at the cancel button, in the copy and in the verify, until the move gets through
    let mut canceled = 0;
    for n in (0..).step_by(4) {
        let mut p = CancelAfter(std::cell::Cell::new(n));
        match dfm::move_to(&located(&fx.env), &fx.env, &to, &linking(), None, &MoveOptions::default(), &mut p) {
            Err(e) => {
                assert_eq!(e.code, ErrorCode::Canceled, "after {n}: {e:?}");
                old_place_intact(&fx, &before, &to);
                canceled += 1;
            }
            Ok(r) => {
                assert_eq!(r.to, to);
                break;
            }
        }
    }
    assert!(canceled >= 10, "{canceled} cancels");
    assert_eq!(located(&fx.env).dirs.root, to);
}

/// A Progress that writes into the source folder while the move copies (at the `at`-th report, or at every one).
struct Writer {
    root: PathBuf,
    n: u32,
    at: Option<u32>,
}
impl Progress for Writer {
    fn report(&mut self, _: Option<f32>, _: &str) {
        self.n += 1;
        if self.at.is_none_or(|a| a == self.n) {
            std::fs::write(self.root.join("settings.json"), format!("{{\"changed\": {}}}\n", self.n)).unwrap();
            put(&self.root, &format!("installs/added-{}.json", self.n), b"{}");
        }
    }
}

#[test]
fn what_changes_in_the_source_during_the_copy_is_copied_again() {
    let fx = Fixture::build("changes");
    let to = fx.base.join("target");
    let mut w = Writer { root: fx.dirs.root.clone(), n: 0, at: Some(3) };
    dfm::move_to(&located(&fx.env), &fx.env, &to, &linking(), None, &MoveOptions::default(), &mut w).unwrap();
    assert_eq!(std::fs::read(to.join("settings.json")).unwrap(), b"{\"changed\": 3}\n", "the changed file");
    assert!(to.join("installs/added-3.json").is_file(), "the added file");
    // a source that keeps changing: the move gives up after its rounds, the old place stays in use
    let fx = Fixture::build("changes-forever");
    let to = fx.base.join("target");
    let mut w = Writer { root: fx.dirs.root.clone(), n: 0, at: None };
    let e = dfm::move_to(&located(&fx.env), &fx.env, &to, &linking(), None, &MoveOptions::default(), &mut w).unwrap_err();
    assert!(e.message.contains("kept changing"), "{e:?}");
    assert_eq!(located(&fx.env).dirs.root, fx.dirs.root);
    assert!(!to.exists() && !fx.dirs.root.join(df::JOURNAL).exists());
}

#[test]
#[cfg(unix)] // Unix permissions; Windows: a file held open without read sharing, tests/data_folder_windows.rs
fn a_source_file_that_cannot_be_read_fails_the_move_naming_it() {
    let fx = Fixture::build("unreadable");
    let before = tree(&fx.dirs.root);
    let f = lib_dir(&fx.dirs, &fx.mods.native).join("fps.dll");
    std::fs::set_permissions(&f, std::fs::Permissions::from_mode(0o000)).unwrap();
    let to = fx.base.join("target");
    let e = mv(&fx, &to, &Facts::all_clear(), &MoveOptions::default()).unwrap_err();
    std::fs::set_permissions(&f, std::fs::Permissions::from_mode(0o644)).unwrap();
    assert!(e.message.contains(&f.display().to_string()), "{e:?}");
    old_place_intact(&fx, &before, &to);
}

/// The process "dies" after each step; a new process then finds what is pending and finishes or undoes it. By hard links
/// and by copies (then each file of the partial copy goes by its sha256 against the old folder's).
#[test]
fn a_crash_at_each_step_is_recovered_by_the_next_start() {
    for (how, facts) in [("links", linking()), ("copies", Facts::all_clear())] {
        for step in [Step::Journal, Step::Marker, Step::Copy, Step::Verify, Step::FileRecord, Step::FolderMarker, Step::Commit, Step::Activate] {
            let fx = Fixture::build(&format!("crash-{how}-{step:?}"));
            let before = tree(&fx.dirs.root);
            let to = fx.base.join("target");
            let e = mv(&fx, &to, &facts, &inject(Inject::StopAfter(step))).unwrap_err();
            assert_eq!(e.code, ErrorCode::Internal, "{step:?}");
            let committed = matches!(step, Step::Commit | Step::Activate);
            match dfm::pending(&fx.env).unwrap() {
                Pending::Unclear { .. } if step == Step::FolderMarker => {
                    let copied = tree(&to);
                    let c = dfm::remove_partial_copy(&fx.env).unwrap();
                    assert!(!c.done && c.removed_bytes == 0, "a commit marker protects the verified copy: {c:?}");
                    assert_eq!(tree(&to), copied);
                    assert_eq!(tree(&fx.dirs.root), before);
                    assert!(fx.dirs.root.join(df::JOURNAL).is_file() && fx.dirs.root.join(df::FILE_RECORD).is_file());
                }
                Pending::Uncommitted { journal } if !committed => {
                    assert_eq!(located(&fx.env).dirs.root, fx.dirs.root, "{step:?}: the old place is in use");
                    assert_eq!(dfm::remove_old_copy(&fx.env, &mut NoProgress).unwrap().removed_files, 0, "{step:?}: nothing of the old place goes");
                    let c = dfm::remove_partial_copy(&fx.env).unwrap();
                    assert!(c.done, "{step:?}: {c:?}");
                    assert_eq!(journal.to, to);
                    old_place_intact(&fx, &before, &to);
                }
                Pending::Committed { journal } if committed => {
                    assert_eq!(located(&fx.env).dirs.root, to, "{step:?}: the new place is in use");
                    assert_eq!(dfm::remove_partial_copy(&fx.env).unwrap().removed_bytes, 0, "{step:?}: nothing of the new place goes");
                    let new = DataDirs::at(&to);
                    // the start-up pass writes the active profiles again for the new place (refresh_active)
                    for g in [&fx.game_z, &fx.game_copy] {
                        profile::refresh_active(&new, g).unwrap();
                    }
                    let f = profile::read_game_profile(&fx.game_z.path).unwrap().unwrap();
                    assert!(f.config_dir.unwrap().contains("target"), "{step:?}: the active profile names the new place");
                    let c = dfm::remove_old_copy(&fx.env, &mut NoProgress).unwrap();
                    assert!(c.done, "{step:?}: {c:?}");
                    assert!(!lib_dir(&fx.dirs, &fx.mods.kiln).exists());
                    assert_eq!(tree(&to).len(), before.len(), "{step:?}: the new place is complete");
                    assert_eq!(journal.from, fx.dirs.root);
                    dfm::acknowledge(&fx.env).unwrap();
                }
                other => panic!("{step:?}: {other:?}"),
            }
        }
    }
}

// ------------------------------------------------------------------------------------------------ removal needs proof

/// A committed move whose own removal of the old copy did not run (the process "died" after the activation).
fn committed_copy(tag: &str) -> (Fixture, PathBuf) {
    let fx = Fixture::build(tag);
    let to = fx.base.join("moved");
    let e = mv(&fx, &to, &linking(), &MoveOptions { force_copy: true, inject: Some(Inject::StopAfter(Step::Activate)) }).unwrap_err();
    assert_eq!(e.code, ErrorCode::Internal);
    assert!(matches!(dfm::pending(&fx.env).unwrap(), Pending::Committed { .. }));
    (fx, to)
}

/// A drive missing when the broken-pointer dialog opens, then mounted (or still missing) at the next start.
/// Reset and repoint must never turn a committed destination into a disposable partial copy.
#[test]
fn a_committed_destination_survives_reset_and_repoint() {
    for action in ["reset", "from", "to"] {
        for mounted in [false, true] {
            let (fx, to) = committed_copy(&format!("committed-{action}-{mounted}"));
            put(&to, "profiles/after-move/data/save.bin", b"written after the move");
            let before = tree(&to);
            let offline = fx.base.join("offline");
            std::fs::rename(&to, &offline).unwrap();
            assert!(fx.env.locate().is_err());
            match action {
                "reset" => { dfm::reset(&fx.env).unwrap(); }
                "from" => { dfm::repoint(&fx.env, &fx.dirs.root, &Facts::all_clear()).unwrap(); }
                "to" => {
                    // A late mount while the dialog is open. Keep the pointer broken until Choose folder.
                    df::write_pointer(&fx.dirs.root, &fx.base.join("missing-drive")).unwrap();
                    std::fs::rename(&offline, &to).unwrap();
                    dfm::repoint(&fx.env, &to, &Facts::all_clear()).unwrap();
                    if !mounted { std::fs::rename(&to, &offline).unwrap(); }
                }
                _ => unreachable!(),
            }
            if mounted && offline.exists() { std::fs::rename(&offline, &to).unwrap(); }
            let pending = dfm::pending(&fx.env).unwrap();
            if action == "to" && mounted {
                assert!(matches!(pending, Pending::Committed { .. }), "{action} {mounted}: {pending:?}");
            } else {
                assert!(matches!(pending, Pending::Unclear { .. }), "{action} {mounted}: {pending:?}");
            }
            let c = dfm::remove_partial_copy(&fx.env).unwrap();
            assert!(!c.done && c.removed_files == 0 && c.removed_bytes == 0, "{action} {mounted}: {c:?}");
            assert!(fx.dirs.root.join(df::JOURNAL).is_file() && fx.dirs.root.join(df::FILE_RECORD).is_file());
            if !mounted {
                assert_eq!(dfm::acknowledge(&fx.env).unwrap_err().code, ErrorCode::Refused, "keep the missing drive's proof");
                std::fs::rename(&offline, &to).unwrap();
            }
            assert_eq!(tree(&to), before, "{action} {mounted}: no destination file was removed");
        }
    }
}

#[test]
fn a_commit_marker_alone_prevents_partial_removal() {
    let fx = Fixture::build("commit-marker");
    let to = fx.base.join("target");
    mv(&fx, &to, &linking(), &inject(Inject::StopAfter(Step::FolderMarker))).unwrap_err();
    let before = tree(&to);
    assert!(matches!(dfm::pending(&fx.env).unwrap(), Pending::Unclear { .. }));
    let c = dfm::remove_partial_copy(&fx.env).unwrap();
    assert!(!c.done && c.reason.as_deref().is_some_and(|r| r.contains(&to.display().to_string())));
    assert_eq!(tree(&to), before);
    assert_eq!(dfm::acknowledge(&fx.env).unwrap_err().code, ErrorCode::Refused);
}

#[test]
fn reset_and_repoint_refuse_unrelated_move_markers() {
    let fx = Fixture::build("recovery-marker");
    df::write_pointer(&fx.dirs.root, &fx.base.join("missing-drive")).unwrap();
    put(&fx.dirs.root, df::MOVE_MARKER, br#"{"format":1,"id":"other"}"#);
    let pointer = std::fs::read(fx.dirs.root.join(df::POINTER)).unwrap();
    for e in [dfm::reset(&fx.env).unwrap_err(), dfm::repoint(&fx.env, &fx.dirs.root, &Facts::all_clear()).unwrap_err()] {
        assert!(e.message.contains(&fx.dirs.root.display().to_string()) && e.message.contains("move"), "{e:?}");
    }
    assert_eq!(std::fs::read(fx.dirs.root.join(df::POINTER)).unwrap(), pointer);
}

#[test]
fn the_old_copy_stays_without_a_journal() {
    let (fx, to) = committed_copy("proof-none");
    std::fs::rename(fx.dirs.root.join(df::JOURNAL), fx.base.join("journal.aside")).unwrap();
    assert_eq!(dfm::remove_old_copy(&fx.env, &mut NoProgress).unwrap_err().code, ErrorCode::NotFound);
    assert!(lib_dir(&fx.dirs, &fx.mods.kiln).exists());
    let _ = to;
}

#[test]
fn the_old_copy_stays_with_a_journal_of_another_move() {
    let (fx, to) = committed_copy("proof-id");
    let mut j = dfm::read_journal(&fx.env).unwrap().unwrap();
    j.id = "0".repeat(32);
    util::write_json_atomic(&fx.dirs.root.join(df::JOURNAL), &j).unwrap();
    let c = dfm::remove_old_copy(&fx.env, &mut NoProgress).unwrap();
    assert_eq!((c.removed_files, c.done), (0, false));
    assert!(c.reason.as_deref().unwrap().contains(&fx.dirs.root.display().to_string()), "it says where the old folder is: {c:?}");
    assert!(lib_dir(&fx.dirs, &fx.mods.kiln).exists());
    let _ = to;
}

#[test]
fn the_old_copy_stays_when_the_new_place_carries_another_moves_marker() {
    let (fx, to) = committed_copy("proof-marker");
    put(&to, df::MOVE_MARKER, serde_json::to_string(&serde_json::json!({"format": 1, "id": "1".repeat(32)})).unwrap().as_bytes());
    let c = dfm::remove_old_copy(&fx.env, &mut NoProgress).unwrap();
    assert_eq!((c.removed_files, c.done), (0, false));
    assert!(c.reason.as_deref().unwrap().contains("marker"), "{c:?}");
    assert!(lib_dir(&fx.dirs, &fx.mods.kiln).exists());
}

#[test]
fn the_old_copy_stays_without_its_file_record() {
    let (fx, _to) = committed_copy("proof-record");
    std::fs::remove_file(fx.dirs.root.join(df::FILE_RECORD)).unwrap();
    let c = dfm::remove_old_copy(&fx.env, &mut NoProgress).unwrap();
    assert_eq!(c.removed_files, 0);
    assert!(c.reason.is_some() && lib_dir(&fx.dirs, &fx.mods.kiln).exists());
}

#[test]
fn the_old_copy_stays_when_the_new_place_is_not_in_use() {
    // the player switched back by hand: the folder in use is not `to`
    let (fx, _to) = committed_copy("proof-in-use");
    df::remove_pointer(&fx.dirs.root).unwrap();
    let c = dfm::remove_old_copy(&fx.env, &mut NoProgress).unwrap();
    assert_eq!(c.removed_files, 0);
    assert!(lib_dir(&fx.dirs, &fx.mods.kiln).exists());
}

#[test]
fn the_old_copy_stays_while_a_game_runs_a_profile_from_it() {
    // a launcher before dev.25 at the default root activated a profile of the old folder
    let (fx, _to) = committed_copy("proof-game");
    let old = DataDirs::at(&fx.dirs.root);
    let theirs = profile::create(&old, "Theirs", None).unwrap().id;
    profile::activate(&old, &theirs, &fx.game_z).unwrap();
    let c = dfm::remove_old_copy(&fx.env, &mut NoProgress).unwrap();
    assert_eq!(c.removed_files, 0);
    assert!(c.reason.as_deref().unwrap().contains(&fx.game_z.path.display().to_string()), "{c:?}");
    assert!(lib_dir(&fx.dirs, &fx.mods.kiln).exists());
}

#[test]
fn an_unrecorded_file_alone_keeps_the_old_folder_in_the_notice() {
    let (fx, _) = committed_copy("proof-addition-only");
    std::fs::remove_dir_all(fx.dirs.root.join("My Notes")).unwrap();
    let added = fx.dirs.root.join("library/kiln/added-by-player.txt");
    std::fs::write(&added, b"mine").unwrap();
    let c = dfm::remove_old_copy(&fx.env, &mut NoProgress).unwrap();
    assert!(c.done && !c.folder_removed, "{c:?}");
    assert_eq!(c.kept.len(), 1, "{c:?}");
    assert_eq!((&c.kept[0].path, c.kept[0].bytes), (&added, 4));
    assert!(c.kept[0].reason.contains("not recorded"));
    assert_eq!(std::fs::read(added).unwrap(), b"mine");
}

#[test]
fn with_proof_what_changed_or_was_added_and_unknown_entries_stay() {
    let (fx, to) = committed_copy("proof-changed");
    let old_m = lib_dir(&fx.dirs, &fx.mods.kiln).join("M.dll");
    std::fs::write(&old_m, b"changed in the old folder").unwrap();
    let new_fps = lib_dir(&DataDirs::at(&to), &fx.mods.native).join("fps.dll");
    std::fs::write(&new_fps, b"changed in the new place").unwrap();
    let old_fps = lib_dir(&fx.dirs, &fx.mods.native).join("fps.dll");
    put(&fx.dirs.root, "library/kiln/added-by-player.txt", b"mine");
    let c = dfm::remove_old_copy(&fx.env, &mut NoProgress).unwrap();
    assert!(c.done);
    let kept: BTreeMap<PathBuf, String> = c.kept.iter().map(|k| (k.path.clone(), k.reason.clone())).collect();
    assert!(kept.get(&old_m).is_some_and(|r| r.contains("old folder")), "{kept:?}");
    assert!(kept.get(&old_fps).is_some_and(|r| r.contains("new place")), "{kept:?}");
    assert!(kept.contains_key(&fx.dirs.root.join("My Notes")));
    assert!(kept.get(&fx.dirs.root.join("library/kiln/added-by-player.txt")).is_some_and(|r| r.contains("not recorded")), "{kept:?}");
    assert!(old_m.is_file() && old_fps.is_file() && fx.dirs.root.join("library/kiln/added-by-player.txt").is_file());
    assert!(fx.dirs.root.join("My Notes/readme.txt").is_file());
    assert!(!lib_dir(&fx.dirs, &fx.mods.emf).exists(), "an identical file in the new place: removed");
    assert!(!fx.dirs.root.join("servers/isles.json").exists(), "the rewritten server record goes by its two hashes");
}

#[cfg(unix)]
#[test]
fn an_unreadable_partial_target_keeps_the_move_records() {
    let fx = Fixture::build("partial-unreadable");
    let to = fx.base.join("disk/moved");
    mv(&fx, &to, &linking(), &inject(Inject::StopAfter(Step::Copy))).unwrap_err();
    let before = tree(&to);
    let parent = to.parent().unwrap();
    std::fs::set_permissions(parent, std::fs::Permissions::from_mode(0o000)).unwrap();
    let result = dfm::remove_partial_copy(&fx.env);
    std::fs::set_permissions(parent, std::fs::Permissions::from_mode(0o700)).unwrap();
    let c = result.unwrap();
    assert!(!c.done && c.reason.is_some(), "an unreadable target is not a target never made: {c:?}");
    assert!(fx.dirs.root.join(df::JOURNAL).is_file(), "keep the recovery proof");
    assert_eq!(tree(&to), before);
}

#[test]
fn a_partial_copy_is_removed_only_with_its_marker() {
    let fx = Fixture::build("partial-proof");
    let to = fx.base.join("target");
    mv(&fx, &to, &linking(), &inject(Inject::StopAfter(Step::Copy))).unwrap_err();
    // the marker is gone (another tool, a player): nothing is removed, the journal stays and names the folder
    std::fs::remove_file(to.join(df::MOVE_MARKER)).unwrap();
    put(&to, "player.txt", b"mine");
    let c = dfm::remove_partial_copy(&fx.env).unwrap();
    assert!(!c.done && c.reason.as_deref().unwrap().contains(&to.display().to_string()), "{c:?}");
    assert!(to.join("library").is_dir() && to.join("player.txt").is_file());
    assert!(fx.dirs.root.join(df::JOURNAL).is_file());
    // with the marker back: what the old folder proves goes; what else is there stays and is named, and so do the
    // folder, the marker and the journal
    let j = dfm::read_journal(&fx.env).unwrap().unwrap();
    put(&to, df::MOVE_MARKER, serde_json::to_string(&serde_json::json!({"format": 1, "id": j.id})).unwrap().as_bytes());
    let c = dfm::remove_partial_copy(&fx.env).unwrap();
    assert!(!c.done && !c.folder_removed, "{c:?}");
    assert_eq!(c.kept.iter().map(|k| (k.path.clone(), k.reason.as_str())).collect::<Vec<_>>(), [(to.join("player.txt"), dfm::partial_reason::NOT_COPIED)]);
    assert!(!to.join("library").exists() && to.join("player.txt").is_file() && to.join(df::MOVE_MARKER).is_file());
    assert!(fx.dirs.root.join(df::JOURNAL).exists());
    // the player takes their file out: the rest goes
    std::fs::remove_file(to.join("player.txt")).unwrap();
    let c = dfm::remove_partial_copy(&fx.env).unwrap();
    assert!(c.done && c.folder_removed && !to.exists(), "{c:?}");
    assert!(!fx.dirs.root.join(df::JOURNAL).exists());
    // a move into a folder of another move in progress is refused
    put(&fx.base, "busy/.kiln-data-move.json", b"{\"format\":1,\"id\":\"x\"}");
    let e = mv(&fx, &fx.base.join("busy"), &linking(), &MoveOptions::default()).unwrap_err();
    assert_eq!(e.detail.as_ref().and_then(|d| d["refusal"].as_str()), Some("move_in_progress"));
}

#[test]
fn a_move_is_refused_while_a_journal_exists() {
    let fx = Fixture::build("journal");
    let to = fx.base.join("target");
    mv(&fx, &to, &linking(), &inject(Inject::StopAfter(Step::Verify))).unwrap_err();
    let e = mv(&fx, &fx.base.join("other"), &linking(), &MoveOptions::default()).unwrap_err();
    assert_eq!(e.detail.as_ref().and_then(|d| d["refusal"].as_str()), Some("move_pending"), "{e:?}");
    assert!(!fx.base.join("other").exists());
}

// ------------------------------------------------------------------------------------------------ after a move

#[cfg(unix)]
fn old_root_forms(root: &Path) -> (String, String, String) {
    let host = root.to_string_lossy().into_owned();
    let z = kiln_manage::winpath::unix_to_z(root).unwrap();
    (host, z.clone(), z.replace('\\', "\\\\"))
}

/// Files below `dir` that name `root` (host path, `Z:` form, JSON-escaped `Z:` form) as a whole path.
#[cfg(unix)]
fn naming(dir: &Path, root: &Path, label: &str, out: &mut BTreeSet<String>) {
    let (host, z, zj) = old_root_forms(root);
    let mut stack = vec![dir.to_path_buf()];
    while let Some(d) = stack.pop() {
        for e in std::fs::read_dir(&d).into_iter().flatten().flatten() {
            let Ok(m) = std::fs::symlink_metadata(e.path()) else { continue };
            if m.is_dir() {
                stack.push(e.path());
            } else if m.is_file() {
                let text = String::from_utf8_lossy(&std::fs::read(e.path()).unwrap()).into_owned();
                let whole = |n: &str| text.match_indices(n).any(|(i, _)| text[i + n.len()..].chars().next().is_none_or(|c| !(c.is_alphanumeric() || "-_.".contains(c))));
                if whole(&host) || whole(&z) || whole(&zj) {
                    out.insert(format!("{label}/{}", e.path().strip_prefix(dir).unwrap().display()));
                }
            }
        }
    }
}

/// After a move of the fixture, nothing the launcher reads names the old root: the rows of recorded-paths.md done.
#[test]
#[cfg(unix)] // Z mode and copy mode, and the Z: forms (Windows: tests/data_folder_windows.rs checks the native paths)
fn after_a_move_nothing_names_the_old_root() {
    let fx = Fixture::build("recorded-after");
    let to = fx.base.join("moved");
    mv(&fx, &to, &linking(), &MoveOptions::default()).unwrap();
    let mut hits = BTreeSet::new();
    naming(&to, &fx.dirs.root, "<data>", &mut hits);
    naming(&fx.game_z.path, &fx.dirs.root, "<game Z>", &mut hits);
    naming(&fx.game_copy.path, &fx.dirs.root, "<game copy>", &mut hits);
    naming(&fx.server, &fx.dirs.root, "<server>", &mut hits);
    naming(&fx.env.state_dir(), &fx.dirs.root, "<state>", &mut hits);
    naming(&fx.env.config_dir(), &fx.dirs.root, "<config>", &mut hits);
    if std::env::var_os("XDG_STATE_HOME").is_some() {
        naming(&util::state_dir(), &fx.dirs.root, "<XDG_STATE_HOME>/kiln", &mut hits);
        naming(&util::config_dir(), &fx.dirs.root, "<XDG_CONFIG_HOME>/kiln", &mut hits);
    }
    assert!(hits.is_empty(), "files that still name the old root: {hits:#?}");
    // and they name the new one where they named the old one
    let mut named = BTreeSet::new();
    naming(&to, &to, "<data>", &mut named);
    naming(&fx.game_z.path, &to, "<game Z>", &mut named);
    naming(&fx.server, &to, "<server>", &mut named);
    let want: BTreeSet<String> = ["<data>/servers/isles.json", "<game Z>/kiln/chain.ini", "<game Z>/kiln/profile.json", "<server>/kiln/data/kiln-network/modpack-build.json"]
        .into_iter()
        .map(String::from)
        .collect();
    assert_eq!(named, want);
    let rec = kiln_manage::game::record(&DataDirs::at(&to), &fx.game_z.id).unwrap().unwrap();
    assert_eq!(rec.install.path_mode, PathMode::Z, "install records written again for the new root");
    let j = dfm::read_journal(&fx.env).unwrap().unwrap();
    assert_eq!(j.rewritten, ["servers/isles.json"], "the journal says the move rewrote it");
}

/// The fixture from the moved folder: the active profile names the new place, activation and deactivation work, and
/// the placement clean-up of dev.24 behaves as before (a Kiln-owned copy goes, a copy the player changed stays).
#[test]
#[cfg(unix)] // Z mode and copy mode
fn the_fixture_works_from_the_moved_folder() {
    let fx = Fixture::build("works-after");
    let to = fx.base.join("moved place");
    let r = mv(&fx, &to, &linking(), &MoveOptions::default()).unwrap();
    let j = dfm::read_journal(&fx.env).unwrap().unwrap();
    assert!(j.rewritten_after.iter().any(|r| r.path.ends_with(".active.json")), "the move's own writes after the switch: {:?}", j.rewritten_after);
    let c = r.cleanup.unwrap();
    assert!(c.done && !c.interrupted, "the move removed the old copy itself: {c:?}");
    assert_eq!(c.kept.iter().map(|k| k.path.clone()).collect::<Vec<_>>(), [fx.dirs.root.join("My Notes")], "only the unknown entry stays: {c:?}");
    assert!(fx.dirs.root.join("My Notes/readme.txt").is_file(), "an unknown entry is never removed");
    assert_eq!(std::fs::read_dir(&fx.dirs.root).unwrap().flatten().map(|e| e.file_name().to_string_lossy().into_owned()).collect::<BTreeSet<_>>(),
        [df::FILE_RECORD, df::JOURNAL, "My Notes", df::POINTER].into_iter().map(String::from).collect::<BTreeSet<_>>(), "the old default root keeps its pointer, the journal and file record until acknowledged, and what the launcher does not know");
    dfm::acknowledge(&fx.env).unwrap();
    let new = DataDirs::at(&to);
    let f = profile::read_game_profile(&fx.game_z.path).unwrap().unwrap();
    let lib = lib_dir(&new, &fx.mods.kiln);
    assert_eq!(f.mods.kiln[0].path, kiln_manage::winpath::unix_to_z(&lib).unwrap());
    assert_eq!(f.config_dir.as_deref(), Some(kiln_manage::winpath::unix_to_z(&new.profile(&fx.active).join("config")).unwrap().as_str()));
    assert_eq!(profile::game_profile_owner(&new, &fx.game_z).unwrap(), GameProfileOwner::Here { id: fx.active.clone() });
    let a = profile::activate(&new, &fx.active, &fx.game_z).unwrap();
    assert_eq!(a.path_mode, PathMode::Z);
    let c = profile::activate(&new, &fx.active, &fx.game_copy).unwrap();
    assert_eq!(c.path_mode, PathMode::Copy);
    // the SET copy in the game is Kiln's: deactivate removes it; in the copy-mode game the player changed it: it stays
    let placed = |g: &kiln_manage::game::GameInstall| g.path.join("shrouded_engine_tweaks/mods/flymode");
    std::fs::write(placed(&fx.game_copy).join("init.lua"), b"print('the player edited this')").unwrap();
    let z = profile::deactivate_report(&new, &fx.game_z).unwrap();
    assert!(!placed(&fx.game_z).exists() && z.retained.is_empty(), "{z:?}");
    let cp = profile::deactivate_report(&new, &fx.game_copy).unwrap();
    assert!(placed(&fx.game_copy).join("init.lua").is_file() && cp.retained.iter().any(|r| r.path == placed(&fx.game_copy)), "{cp:?}");
    assert!(profile::read_game_profile(&fx.game_z.path).unwrap().is_none());
}

// ------------------------------------------------------------------------------------------------ another data folder's profile

/// After a move, a launcher before dev.25 runs at the default root as on a first run and activates a profile of its own
/// in a game. The moved folder never takes it over and never mixes the two (Z mode and copy mode, another id and the
/// same id), and says whose it is.
#[test]
#[cfg(unix)] // Z mode and copy mode
fn another_data_folders_profile_is_left_alone() {
    for same_id in [false, true] {
        let fx = Fixture::build(&format!("other-profile-{same_id}"));
        let to = fx.base.join("moved");
        mv(&fx, &to, &linking(), &MoveOptions::default()).unwrap();
        dfm::remove_old_copy(&fx.env, &mut NoProgress).unwrap();
        dfm::acknowledge(&fx.env).unwrap();
        let new = DataDirs::at(&to);
        // the launcher before dev.25, at the default root (it does not read the pointer)
        let old = DataDirs::at(&fx.dirs.root);
        let name = if same_id { "Ember run" } else { "Other" };
        let theirs = profile::create(&old, name, None).unwrap().id;
        assert_eq!(theirs == fx.active, same_id);
        for g in [&fx.game_z, &fx.game_copy] {
            profile::activate(&old, &theirs, g).unwrap();
        }
        put(&fx.game_copy.path, &format!("kiln/profiles/{theirs}/config/new-items.json"), b"{\"for\":\"the default root's profile\"}");
        for g in [&fx.game_z, &fx.game_copy] {
            let before = std::fs::read(g.path.join("kiln/profile.json")).unwrap();
            assert!(profile::refresh_active(&new, g).unwrap().is_none(), "{same_id} {}: not taken over", g.id);
            assert!(quick::resume_active(&new, g).unwrap().is_none());
            assert_eq!(std::fs::read(g.path.join("kiln/profile.json")).unwrap(), before, "the game's profile file is untouched");
            let owner = profile::game_profile_owner(&new, g).unwrap();
            let want_root = (g.path_mode == PathMode::Z).then(|| fx.dirs.root.clone());
            assert_eq!(owner, GameProfileOwner::Elsewhere { id: theirs.clone(), root: want_root }, "{same_id} {}", g.id);
            assert_eq!(profile::game_profile_owner(&old, g).unwrap(), GameProfileOwner::Here { id: theirs.clone() }, "and the other folder still sees its own");
        }
        // copy mode: the game's config copy is the other folder's, never copied into this one
        assert_eq!(profile::copy_back(&new, &theirs, &fx.game_copy).unwrap(), 0);
        if same_id {
            assert!(!new.profile(&fx.active).join("config/new-items.json").exists(), "nothing mixed into the moved profile");
            // the player activates the moved folder's profile in that game: the other folder's config copy is kept aside
            let a = profile::activate(&new, &fx.active, &fx.game_copy).unwrap();
            assert!(a.warnings.iter().any(|w| w.contains("another data folder")), "{:?}", a.warnings);
            assert!(!new.profile(&fx.active).join("config/new-items.json").exists());
            let kept: Vec<_> = std::fs::read_dir(fx.game_copy.path.join("kiln/backups")).unwrap().flatten()
                .filter(|e| e.file_name().to_string_lossy().starts_with("profile-config-")).map(|e| e.path())
                .filter(|p| std::fs::read(p.join("new-items.json")).is_ok_and(|b| b == b"{\"for\":\"the default root's profile\"}")).collect();
            assert_eq!(kept.len(), 1, "the other folder's config, kept whole");
            assert_eq!(profile::game_profile_owner(&new, &fx.game_copy).unwrap(), GameProfileOwner::Here { id: fx.active.clone() });
        }
    }
}

/// A launcher that never moved anything: its own profile.json is its own, in Z and copy mode, also after an activation
/// record went missing (the id decides then, as before).
#[test]
fn a_launcher_that_never_moved_keeps_its_own_profiles() {
    let fx = Fixture::build("never-moved");
    for g in [&fx.game_z, &fx.game_copy] {
        assert_eq!(profile::game_profile_owner(&fx.dirs, g).unwrap(), GameProfileOwner::Here { id: fx.active.clone() });
        std::fs::remove_file(fx.dirs.installs().join(format!("{}.active.json", g.id))).unwrap();
        assert_eq!(profile::game_profile_owner(&fx.dirs, g).unwrap(), GameProfileOwner::Here { id: fx.active.clone() }, "{}", g.id);
        let generation = profile::read_game_profile(&g.path).unwrap().unwrap().generation;
        profile::refresh_active(&fx.dirs, g).unwrap();
        assert_eq!(profile::read_game_profile(&g.path).unwrap().unwrap().generation, generation, "current: not written again");
    }
}

// ------------------------------------------------------------------------------------------------ core-3

/// A move into a default root that keeps a folder of its own under an entry's name. On Windows that is `state\`
/// (`util::state_dir()`, server console captures); with Linux rules, `XDG_STATE_HOME` set to the default root makes its
/// `kiln/` that folder. The entry is not copied, and the target's folder is untouched, whether the move completes, fails
/// after the copy or is canceled.
#[test]
fn a_target_keeps_its_own_folder_whatever_the_move_does() {
    // Windows: `state`, Kiln's state folder in the default root as the platform has it
    let own = if cfg!(windows) { "state" } else { "kiln" };
    for case in ["done", "fail", "cancel"] {
        let s = scratch(&format!("own-{case}"));
        let mut env = env_at(&s.0);
        let default = env.default_root().unwrap();
        if !cfg!(windows) {
            env.xdg_state_home = Some(default.clone().into());
        }
        assert_eq!(env.state_dir(), default.join(own));
        put(&default, &format!("{own}/server.run.log"), b"console of a running server");
        let away = s.0.join("away");
        put(&away, "library/kiln/a/1.0.0+00000000/M.dll", b"mod");
        put(&away, &format!("{own}/notes.txt"), b"an entry of the same name");
        df::write_pointer(&default, &away).unwrap();
        assert_eq!(df::own_entries(&default, &env), [own]);
        assert_eq!(df::recognize(&default, &env).unwrap(), FolderState::Empty);
        let opts = if case == "fail" { inject(Inject::FailAfterCopy) } else { MoveOptions::default() };
        let mut cancel = CancelAfter(std::cell::Cell::new(0));
        let mut none = NoProgress;
        let p: &mut dyn Progress = if case == "cancel" { &mut cancel } else { &mut none };
        let r = dfm::move_to(&env.locate().unwrap(), &env, &default, &linking(), None, &opts, p);
        assert_eq!(std::fs::read(default.join(own).join("server.run.log")).unwrap(), b"console of a running server", "{case}");
        assert_eq!(std::fs::read_dir(default.join(own)).unwrap().count(), 1, "{case}: nothing was copied into it");
        if case == "done" {
            let r = r.unwrap();
            assert!(r.stayed.iter().any(|s| s.name == own && s.path == away.join(own)), "{r:?}");
            assert!(away.join(own).join("notes.txt").is_file(), "the entry stays in the old folder");
            assert!(default.join("library/kiln/a/1.0.0+00000000/M.dll").is_file());
            assert!(r.cleanup.unwrap().kept.iter().any(|k| k.path == away.join(own)));
        } else {
            assert!(r.is_err(), "{case}");
            assert_eq!(env.locate().unwrap().dirs.root, away, "{case}: the old place is in use");
            assert!(!default.join("library").exists() && !default.join(df::MOVE_MARKER).exists(), "{case}: what the move made is gone");
        }
    }
}

/// A file that appears in the target while the move copies is not the move's: the move stops (`conflict`, naming it),
/// removes what it made, and leaves that file where it is.
#[test]
fn a_move_never_replaces_or_removes_what_it_did_not_make() {
    struct Intruder {
        at: PathBuf,
        put: bool,
    }
    impl Progress for Intruder {
        fn report(&mut self, _: Option<f32>, m: &str) {
            if !self.put && m.starts_with("copying") {
                put(self.at.parent().unwrap(), "settings.json", b"someone else's");
                self.put = true;
            }
        }
    }
    let fx = Fixture::build("intruder");
    let before = tree(&fx.dirs.root);
    let to = fx.base.join("target");
    let mut p = Intruder { at: to.join("settings.json"), put: false };
    let e = dfm::move_to(&located(&fx.env), &fx.env, &to, &linking(), None, &MoveOptions::default(), &mut p).unwrap_err();
    assert_eq!(e.code, ErrorCode::Conflict, "{e:?}");
    assert!(e.message.contains("settings.json"), "{e:?}");
    assert_eq!(std::fs::read(to.join("settings.json")).unwrap(), b"someone else's");
    let left: Vec<String> = std::fs::read_dir(&to).unwrap().flatten().map(|e| e.file_name().to_string_lossy().into_owned()).collect();
    assert_eq!(left, ["settings.json"], "only what the move did not make is left");
    assert_eq!(tree(&fx.dirs.root), before);
    assert_eq!(located(&fx.env).dirs.root, fx.dirs.root);
    assert!(!fx.dirs.root.join(df::JOURNAL).exists());
}

/// `rewrittenAfter` holds only what the move writes (install and activation records, the activated profiles'
/// configs): a file another writer changed after the switch is not in it, and its old copy stays.
#[test]
fn only_what_the_move_writes_after_the_switch_is_rewritten_after() {
    struct Other {
        to: PathBuf,
    }
    impl Progress for Other {
        fn report(&mut self, _: Option<f32>, m: &str) {
            if m.starts_with("writing the games' profiles") {
                std::fs::write(self.to.join("settings.json"), b"{\"written\": \"by someone else\"}\n").unwrap();
            }
        }
    }
    let fx = Fixture::build("rewritten-after");
    let to = fx.base.join("moved");
    let r = dfm::move_to(&located(&fx.env), &fx.env, &to, &linking(), None, &MoveOptions::default(), &mut Other { to: to.clone() }).unwrap();
    let j = dfm::read_journal(&fx.env).unwrap().unwrap();
    let paths: BTreeSet<&str> = j.rewritten_after.iter().map(|r| r.path.as_str()).collect();
    assert!(!paths.contains("settings.json"), "{paths:?}");
    assert!(paths.iter().all(|p| p.starts_with("installs/") || p.contains("/config/")), "{paths:?}");
    assert!(paths.contains(format!("installs/{}.active.json", fx.game_z.id).as_str()), "{paths:?}");
    let c = r.cleanup.unwrap();
    assert!(c.kept.iter().any(|k| k.path == fx.dirs.root.join("settings.json")), "{c:?}");
    assert!(fx.dirs.root.join("settings.json").is_file());
}

/// The games to activate again are decided with the old root, before the copy: a profile an older launcher activated
/// (no activation record) is written again for the new place, and the old copy goes.
#[test]
fn a_profile_without_an_activation_record_is_activated_again() {
    let fx = Fixture::build("no-record");
    std::fs::remove_file(fx.dirs.installs().join(format!("{}.active.json", fx.game_z.id))).unwrap();
    let to = fx.base.join("moved");
    let r = mv(&fx, &to, &linking(), &MoveOptions::default()).unwrap();
    assert!(r.activated.contains(&fx.game_z.id), "{r:?}");
    let f = profile::read_game_profile(&fx.game_z.path).unwrap().unwrap();
    assert!(f.config_dir.unwrap().contains("moved"), "the game's profile names the new place");
    let c = r.cleanup.unwrap();
    assert!(c.done && c.reason.is_none(), "{c:?}");
    assert!(!lib_dir(&fx.dirs, &fx.mods.kiln).exists(), "the old copy went");
}

/// At the top of a data folder only `quick.lock` and `data-folder.lock` are lock files: another `*.lock` is an entry
/// the launcher does not know (copied, kept).
#[test]
fn only_the_two_lock_files_are_locks_at_the_top() {
    let fx = Fixture::build("locks");
    put(&fx.dirs.root, "notes.lock", b"the player's");
    let to = fx.base.join("moved");
    let r = mv(&fx, &to, &linking(), &MoveOptions::default()).unwrap();
    assert_eq!(std::fs::read(to.join("notes.lock")).unwrap(), b"the player's", "copied");
    assert_eq!(std::fs::read(fx.dirs.root.join("notes.lock")).unwrap(), b"the player's", "kept in the old folder");
    assert!(r.stayed.iter().any(|s| s.name == "notes.lock"));
    assert!(!to.join("quick.lock").exists(), "a lock file is never copied");
    assert!(!fx.dirs.root.join("quick.lock").exists(), "and goes with the old copy");
}

/// Another data folder's profile in a game is said in the game's status, not shown as loaded, and a save backup taken
/// then is not filed under this folder's profile of the same id.
#[test]
fn another_folders_profile_is_said_in_the_status_and_its_saves_are_not_filed_here() {
    use kiln_manage::game::{self, OtherFolderProfile};
    let fx = Fixture::build("status-other");
    let st = game::status_with(&fx.dirs, &fx.game_z, &[]).unwrap();
    assert_eq!((st.active_profile.clone(), st.other_folder_profile.clone()), (Some(fx.active.clone()), None));
    assert!(serde_json::to_value(&st).unwrap().get("otherFolderProfile").is_none(), "left out when there is none");
    let other = DataDirs::at(fx.base.join("other data"));
    let theirs = profile::create(&other, "Ember run", None).unwrap().id;
    assert_eq!(theirs, fx.active, "the same id");
    profile::activate(&other, &theirs, &fx.game_z).unwrap();
    let st = game::status_with(&fx.dirs, &fx.game_z, &[]).unwrap();
    assert_eq!(st.active_profile, None);
    assert_eq!(st.other_folder_profile, Some(OtherFolderProfile { id: theirs.clone(), root: Some(other.root.clone()) }));
    assert_eq!(serde_json::to_value(&st).unwrap()["otherFolderProfile"]["id"], theirs.as_str());
    let dir = kiln_manage::saves::SaveDir { label: "local".into(), path: fx.saves_dir.clone(), steam_id: None, recursive: true };
    let b = kiln_manage::saves::backup_from(&fx.dirs, &fx.game_z, &[dir], kiln_manage::saves::BackupReason::Manual, None, None).unwrap();
    assert!(fx.dirs.backups().join("saves").join(&fx.game_z.id).join(&b.id).join("backup.json").is_file());
    assert!(!fx.dirs.profile(&fx.active).join("save-backups").join(&b.id).exists());
}

/// Choosing a Kiln data folder after a broken pointer: only then, only a Kiln data folder by its layout, with the place
/// refusals that need no folder in use; the pointer is the only thing it writes in the default root.
#[test]
fn repoint_after_a_broken_pointer() {
    let fx = Fixture::build("repoint");
    let default = fx.dirs.root.clone();
    let all = Facts::all_clear();
    let mine = fx.base.join("my kiln");
    put(&mine, "profiles/elsewhere/meta.json", b"{}");
    df::write_pointer(&default, &mine).unwrap();
    assert_eq!(dfm::repoint(&fx.env, &mine, &all).unwrap_err().code, ErrorCode::Refused, "the pointer works");
    let with_env = DataEnv { kiln_data_dir: Some(fx.base.join("env").into()), ..fx.env.clone() };
    assert_eq!(dfm::repoint(&with_env, &mine, &all).unwrap_err().code, ErrorCode::Refused, "KILN_DATA_DIR is set");
    // the folder went to another disk: the pointer is broken
    let found = fx.base.join("found again");
    std::fs::rename(&mine, &found).unwrap();
    assert!(fx.env.locate().is_err());
    let names = |d: &Path| -> BTreeSet<String> {
        std::fs::read_dir(d).unwrap().flatten().map(|e| e.file_name().to_string_lossy().into_owned()).filter(|n| n != df::POINTER).collect()
    };
    let entries = names(&default);
    let pointer = std::fs::read(default.join(df::POINTER)).unwrap();
    let refusal = |p: &Path, facts: &Facts| {
        let e = dfm::repoint(&fx.env, p, facts).unwrap_err();
        assert_eq!(std::fs::read(default.join(df::POINTER)).unwrap(), pointer, "{p:?}: the pointer is as it was");
        e.detail.and_then(|d| d["refusal"].as_str().map(str::to_string)).unwrap_or_else(|| format!("{:?}", e.code))
    };
    assert_eq!(refusal(Path::new("relative/kiln"), &all), "not_absolute");
    assert_eq!(refusal(&fx.base.join("a/../found again"), &all), "parent_traversal");
    put(&fx.base, "a-file", b"x");
    assert_eq!(refusal(&fx.base.join("a-file"), &all), "not_a_folder");
    #[cfg(unix)] // Windows: a junction, tests/data_folder_windows.rs
    {
        symlink(&found, fx.base.join("a link")).unwrap();
        assert_eq!(refusal(&fx.base.join("a link"), &all), "link");
    }
    std::fs::create_dir_all(fx.base.join("empty")).unwrap();
    assert_eq!(refusal(&fx.base.join("empty"), &all), "not_a_kiln_folder");
    put(&fx.base, "moving/.kiln-data-move.json", b"{\"format\":1,\"id\":\"x\"}");
    assert_eq!(refusal(&fx.base.join("moving"), &all), "move_in_progress");
    put(&fx.game_z.path, "kiln-data/installs/x.json", b"{}");
    assert_eq!(refusal(&fx.game_z.path.join("kiln-data"), &all), "inside_game");
    put(&fx.env.state_dir(), "kiln-data/installs/x.json", b"{}");
    assert_eq!(refusal(&fx.env.state_dir().join("kiln-data"), &all), "inside_kiln_state");
    assert_eq!(refusal(&found, &Facts { writable: false, ..Facts::all_clear() }), "not_writable");
    assert_eq!(names(&default), entries);
    // repaired to the folder where it is now
    let l = dfm::repoint(&fx.env, &found, &all).unwrap();
    assert_eq!((l.dirs.root.clone(), l.source), (found.clone(), RootSource::Pointer));
    assert_eq!(names(&default), entries, "nothing but the pointer changed in the default root");
    // and to the default root
    df::write_pointer(&default, &fx.base.join("gone")).unwrap();
    let l = dfm::repoint(&fx.env, &default, &all).unwrap();
    assert_eq!((l.dirs.root.clone(), l.source), (default.clone(), RootSource::Default));
    assert!(!default.join(df::POINTER).exists());
    assert_eq!(names(&default), entries, "no lock file, no journal");
}

// ------------------------------------------------------------------------------------------------ core-4

/// A file to hard-link is read first: one that cannot be read fails the move before anything of it is in the target.
/// No link to it is ever made (its ctime, which a link and the link's removal would both change, stays as it was).
/// The target is as it was (gone: the move made it), and the same move goes through once the file can be read.
#[test]
#[cfg(unix)] // Unix permissions; Windows: a file held open without read sharing, tests/data_folder_windows.rs
fn a_file_that_cannot_be_read_is_never_linked() {
    let fx = Fixture::build("unreadable-link");
    let before = tree(&fx.dirs.root);
    let f = lib_dir(&fx.dirs, &fx.mods.native).join("fps.dll");
    std::fs::set_permissions(&f, std::fs::Permissions::from_mode(0o000)).unwrap();
    let ctime = |p: &Path| std::fs::symlink_metadata(p).map(|m| (m.ctime(), m.ctime_nsec())).unwrap();
    let was = ctime(&f);
    std::thread::sleep(std::time::Duration::from_millis(30)); // a link made from here on has another ctime
    let to = fx.base.join("target");
    let e = mv(&fx, &to, &linking(), &MoveOptions::default()).unwrap_err();
    assert_eq!(ctime(&f), was, "no link to the unreadable file was made (and removed)");
    std::fs::set_permissions(&f, std::fs::Permissions::from_mode(0o644)).unwrap();
    assert!(e.message.contains(&f.display().to_string()), "{e:?}");
    old_place_intact(&fx, &before, &to);
    let r = mv(&fx, &to, &linking(), &MoveOptions::default()).unwrap();
    assert!(r.linked > 0);
    assert_eq!(located(&fx.env).dirs.root, to);
}

/// A copy that fails in the middle (the disk fills up after the destination was made and partly written) leaves
/// nothing behind; the same move then goes through. By real copies, and by hard links (where only what is written in
/// place is copied). A target folder that was there before (empty) stays, empty.
#[test]
fn a_copy_that_fails_in_the_middle_leaves_nothing_behind() {
    for (tag, facts, n, existed) in [("copies", Facts::all_clear(), 3, false), ("links", linking(), 0, false), ("existing", Facts::all_clear(), 5, true)] {
        let fx = Fixture::build(&format!("full-during-{tag}"));
        let before = tree(&fx.dirs.root);
        let to = fx.base.join("target");
        if existed {
            std::fs::create_dir_all(&to).unwrap();
        }
        let e = mv(&fx, &to, &facts, &inject(Inject::FullDuring(n))).unwrap_err();
        assert!(e.message.contains("no space left"), "{tag}: {e:?}");
        if existed {
            assert_eq!(std::fs::read_dir(&to).unwrap().count(), 0, "{tag}: the target is as it was");
            std::fs::remove_dir(&to).unwrap(); // for the check below, then made again
            old_place_intact(&fx, &before, &to);
            std::fs::create_dir_all(&to).unwrap();
        } else {
            old_place_intact(&fx, &before, &to);
        }
        mv(&fx, &to, &facts, &MoveOptions::default()).unwrap();
        assert_eq!(located(&fx.env).dirs.root, to, "{tag}");
    }
}

/// A full disk in the middle of each file of a copy in turn (a server record the copy rewrites too): every time the
/// target is gone and the old place complete and in use, until the move goes through.
#[test]
fn a_full_disk_in_the_middle_of_any_file_leaves_nothing_behind() {
    let mut n = 0;
    loop {
        let fx = Fixture::build(&format!("full-each-{n}"));
        let before = tree(&fx.dirs.root);
        let to = fx.base.join("target");
        match mv(&fx, &to, &Facts::all_clear(), &inject(Inject::FullDuring(n))) {
            Err(e) => {
                assert!(e.message.contains("no space left"), "file {n}: {e:?}");
                old_place_intact(&fx, &before, &to);
            }
            Ok(r) => {
                assert!(r.copied > 5 && n == r.copied, "every file was the one the disk filled up on once: {n} of {}", r.copied);
                break;
            }
        }
        n += 1;
        assert!(n < 500);
    }
}

/// Makes the Z game's `kiln/` read-only as the switch starts, so writing its profile for the new place fails after the
/// commit.
#[cfg(unix)]
struct BlockGame {
    kiln: PathBuf,
}
#[cfg(unix)]
impl Progress for BlockGame {
    fn report(&mut self, _: Option<f32>, m: &str) {
        if m.starts_with("switching") {
            std::fs::set_permissions(&self.kiln, std::fs::Permissions::from_mode(0o555)).unwrap();
        }
    }
}

/// Every path a game's profile file names (config and data folder, mod folders, plugin DLLs), as host paths.
#[cfg(unix)]
fn named_paths(g: &kiln_manage::game::GameInstall, file: &kiln_manage::profile::ProfileFile) -> Vec<PathBuf> {
    let mut v: Vec<&str> = file.config_dir.iter().chain(&file.data_dir).map(String::as_str).collect();
    for k in kiln_manage::manifest::ModKind::ALL {
        v.extend(file.mods.folders(k).into_iter().flatten().map(|m| m.path.as_str()));
    }
    v.extend(file.mods.plugin.iter().map(|m| m.path.as_str()));
    v.into_iter().map(|p| kiln_manage::winpath::to_host(p, g).unwrap()).collect()
}

/// A game's profile that could not be written for the new place after the commit still names the old copy: the move
/// does not remove it, nor does a later start, until that profile is activated again from the new place.
#[test]
#[cfg(unix)] // Z mode, and a read-only folder
fn the_old_copy_stays_while_a_game_still_names_it() {
    let fx = Fixture::build("names-old");
    let before = tree(&fx.dirs.root);
    let to = fx.base.join("moved");
    let kiln = fx.game_z.path.join("kiln");
    let mut p = BlockGame { kiln: kiln.clone() };
    let r = dfm::move_to(&located(&fx.env), &fx.env, &to, &linking(), None, &MoveOptions::default(), &mut p);
    std::fs::set_permissions(&kiln, std::fs::Permissions::from_mode(0o755)).unwrap();
    let r = r.unwrap();
    let game = fx.game_z.path.display().to_string();
    assert!(!r.activated.contains(&fx.game_z.id) && r.activated.contains(&fx.game_copy.id), "{r:?}");
    assert!(r.warnings.iter().any(|w| w.contains(&game)), "{:?}", r.warnings);
    let c = r.cleanup.unwrap();
    assert!(!c.done && c.removed_files == 0, "the move did not run its removal: {c:?}");
    assert!(c.reason.as_deref().is_some_and(|w| w.contains(&game) && w.contains("could not be written")), "{c:?}");
    assert!(fx.dirs.root.join(df::JOURNAL).exists() && fx.dirs.root.join(df::FILE_RECORD).exists(), "the journal stays for the next start");
    assert_eq!(tree(&fx.dirs.root), before, "the old copy is complete");
    let f = profile::read_game_profile(&fx.game_z.path).unwrap().unwrap();
    let paths = named_paths(&fx.game_z, &f);
    assert!(paths.iter().all(|p| p.exists() && !p.starts_with(&to)), "every path the game's profile names is there, none in the new place: {paths:?}");
    assert!(paths.iter().filter(|p| p.starts_with(&fx.dirs.root)).count() >= 3, "it reads from the old copy: {paths:?}");
    // the next start: the old copy stays, and the result says which game
    assert!(matches!(dfm::pending(&fx.env).unwrap(), Pending::Committed { .. }));
    let c = dfm::remove_old_copy(&fx.env, &mut NoProgress).unwrap();
    assert!(c.removed_files == 0 && c.reason.as_deref().is_some_and(|w| w.contains(&game) && w.contains("activate")), "{c:?}");
    assert_eq!(tree(&fx.dirs.root), before);
    // the profile activated again from the new place: the old copy goes
    profile::activate(&DataDirs::at(&to), &fx.active, &fx.game_z).unwrap();
    let c = dfm::remove_old_copy(&fx.env, &mut NoProgress).unwrap();
    assert!(c.done && c.removed_files > 0, "{c:?}");
    assert!(!lib_dir(&fx.dirs, &fx.mods.kiln).exists());
    let f = profile::read_game_profile(&fx.game_z.path).unwrap().unwrap();
    let paths = named_paths(&fx.game_z, &f);
    assert!(paths.iter().all(|p| p.exists() && !p.starts_with(&fx.dirs.root)), "{paths:?}");
}

/// A play-once profile that names the old copy holds it too, whoever wrote it.
#[test]
#[cfg(unix)] // Z mode
fn a_play_once_profile_that_names_the_old_copy_holds_it() {
    let fx = Fixture::build("once");
    // the game's profile file as the old place wrote it, kept to be put back as a play-once profile after the move
    let old_profile = std::fs::read(fx.game_z.path.join("kiln/profile.json")).unwrap();
    let to = fx.base.join("moved");
    let e = mv(&fx, &to, &linking(), &MoveOptions { force_copy: false, inject: Some(Inject::StopAfter(Step::Activate)) }).unwrap_err();
    assert_eq!(e.code, ErrorCode::Internal);
    std::fs::write(fx.game_z.path.join("kiln/profile.once.json"), &old_profile).unwrap();
    let c = dfm::remove_old_copy(&fx.env, &mut NoProgress).unwrap();
    assert!(c.removed_files == 0 && c.reason.as_deref().is_some_and(|w| w.contains("profile.once.json")), "{c:?}");
    assert!(lib_dir(&fx.dirs, &fx.mods.kiln).exists());
    std::fs::remove_file(fx.game_z.path.join("kiln/profile.once.json")).unwrap();
    let c = dfm::remove_old_copy(&fx.env, &mut NoProgress).unwrap();
    assert!(c.done && c.removed_files > 0, "{c:?}");
}

// ------------------------------------------------------------------------------------------------ core-5

/// What a failed clean-up leaves keeps its proof: the target's move marker and the journal (with the result) stay, the
/// folder reads as a move in progress (never a target, never "use this one"), the old place is complete and in use,
/// and the next start removes the rest once it can; then the same move goes through.
fn left_with_its_proof(fx: &Fixture, before: &BTreeMap<String, String>, to: &Path, e: &kiln_manage::Error, left: &Path, os: &str) {
    assert!(e.message.contains(&left.display().to_string()) && e.message.contains(&to.display().to_string()), "the error names what stayed and where: {e:?}");
    assert!(e.message.contains(os), "with the OS error: {e:?}");
    assert_eq!(&tree(&fx.dirs.root), before, "the old place is complete");
    assert_eq!(located(&fx.env).dirs.root, fx.dirs.root, "and in use");
    assert!(!fx.dirs.root.join(df::POINTER).exists());
    assert!(to.join(df::MOVE_MARKER).is_file(), "the target keeps the move's marker");
    assert!(matches!(df::recognize(to, &fx.env).unwrap(), FolderState::Moving { .. }));
    let Pending::Uncommitted { journal } = dfm::pending(&fx.env).unwrap() else { panic!("the journal stays: {:?}", dfm::pending(&fx.env)) };
    let kept = journal.result.as_ref().map(|r| r.kept.clone()).unwrap_or_default();
    assert!(kept.iter().any(|k| k.path == left && k.reason.contains(os)), "the journal's result names it, with the OS error: {kept:?}");
    // never a target, never "use this one": here a move is pending, and for another launcher the folder is a move in
    // progress
    let refusal = |e: kiln_manage::Error| e.detail.as_ref().and_then(|d| d["refusal"].as_str()).map(str::to_string);
    assert_eq!(refusal(mv(fx, to, &Facts::all_clear(), &MoveOptions::default()).unwrap_err()).as_deref(), Some("move_pending"));
    let other = Fixture::build(&format!("another-launcher-{}", to.parent().unwrap().file_name().unwrap().to_string_lossy()));
    let r = df::check_here(&located(&other.env), &other.env, to, false).unwrap().unwrap_err();
    assert_eq!(r.code, df::RefusalCode::MoveInProgress, "{r:?}");
    let e = dfm::use_folder(&located(&other.env), &other.env, to, &Facts::all_clear(), None).unwrap_err();
    assert_eq!(refusal(e).as_deref(), Some("move_in_progress"));
    // the launcher cannot drop the proof by acknowledging the result
    let e = dfm::acknowledge(&fx.env).unwrap_err();
    assert_eq!(e.code, ErrorCode::Refused, "{e:?}");
    assert!(fx.dirs.root.join(df::JOURNAL).is_file());
}

fn the_next_start_removes_the_rest(fx: &Fixture, before: &BTreeMap<String, String>, to: &Path, facts: &Facts) {
    let c = dfm::remove_partial_copy(&fx.env).unwrap();
    assert!(c.done && c.kept.is_empty(), "{c:?}");
    old_place_intact(fx, before, to);
    mv(fx, to, facts, &MoveOptions::default()).unwrap();
    assert_eq!(located(&fx.env).dirs.root, to);
}

/// Makes a folder of the target read-only when the move reports copying the first file in `when` (`deep`: or below it),
/// then cancels the move if `cancel`.
#[cfg(unix)]
struct ReadOnlyAt {
    when: String,
    deep: bool,
    dir: PathBuf,
    cancel: bool,
    done: bool,
}
#[cfg(unix)]
impl Progress for ReadOnlyAt {
    fn report(&mut self, _: Option<f32>, m: &str) {
        let here = m.strip_prefix("copying ").and_then(|r| r.strip_prefix(&self.when)).is_some_and(|f| self.deep || !f.contains('/'));
        if !self.done && here {
            std::fs::set_permissions(&self.dir, std::fs::Permissions::from_mode(0o555)).unwrap();
            self.done = true;
        }
    }
    fn canceled(&self) -> bool {
        self.cancel && self.done
    }
}

/// A file the move made that cannot be removed again (its folder in the target became read-only after the copy
/// began), and a folder the move made that cannot be removed (its parent became read-only): the move fails naming it,
/// keeps its proof, and the next start removes the rest once the folder can be written again.
#[test]
#[cfg(unix)] // a read-only folder: Linux cannot deny a delete through a handle
fn a_clean_up_that_leaves_anything_keeps_its_proof() {
    for case in ["file", "folder"] {
        let fx = Fixture::build(&format!("proof-{case}"));
        let before = tree(&fx.dirs.root);
        let to = fx.base.join("target");
        let (when, deep, cancel, left) = if case == "file" {
            // a library folder with two files or more: after its first file the folder is read-only, the second one
            // cannot be made there, and the first one cannot be removed again
            let mut per: BTreeMap<String, Vec<String>> = BTreeMap::new();
            for k in before.keys().filter(|k| k.starts_with("library/")) {
                let (d, f) = k.rsplit_once('/').unwrap();
                per.entry(d.to_string()).or_default().push(f.to_string());
            }
            let (d, files) = per.into_iter().find(|(_, f)| f.len() >= 2).expect("a library folder with two files");
            (format!("{d}/"), false, false, to.join(&d).join(&files[0]))
        } else {
            // library/native read-only after the first file below it, then a cancel: library/native/fps cannot go
            ("library/native/".to_string(), true, true, to.join("library/native/fps"))
        };
        let dir = to.join(when.trim_end_matches('/'));
        let mut p = ReadOnlyAt { when, deep, dir: dir.clone(), cancel, done: false };
        let e = dfm::move_to(&located(&fx.env), &fx.env, &to, &Facts::all_clear(), None, &MoveOptions::default(), &mut p).unwrap_err();
        assert!(p.done, "{case}: the folder was made read-only");
        assert!(left.exists(), "{case}: {} stays", left.display());
        left_with_its_proof(&fx, &before, &to, &e, &left, "ermission denied");
        // the next start, while the folder is still read-only: nothing more goes, the proof stays
        let c = dfm::remove_partial_copy(&fx.env).unwrap();
        assert!(!c.done && !c.kept.is_empty() && to.join(df::MOVE_MARKER).is_file(), "{case}: {c:?}");
        assert!(fx.dirs.root.join(df::JOURNAL).is_file());
        std::fs::set_permissions(&dir, std::fs::Permissions::from_mode(0o755)).unwrap();
        the_next_start_removes_the_rest(&fx, &before, &to, &Facts::all_clear());
    }
}

/// A file a copy step made, failed in (a full disk) and cannot remove again (another program opened it without delete
/// sharing; a test hook): it stays known to the move, the error names it and its folder, the clean-up keeps its proof,
/// and the next start (another process: the file is free) removes it.
#[test]
fn a_made_file_that_cannot_be_removed_again_stays_named() {
    let fx = Fixture::build("held-made");
    let before = tree(&fx.dirs.root);
    let to = fx.base.join("target");
    let e = mv(&fx, &to, &Facts::all_clear(), &inject(Inject::HeldDuring(3))).unwrap_err();
    assert!(e.message.contains("no space left") && e.message.contains("could not remove it again"), "{e:?}");
    let c: dfm::Cleanup = serde_json::from_value(e.detail.clone().unwrap()).unwrap();
    assert_eq!(c.kept.len(), 1, "{c:?}");
    let left = c.kept[0].path.clone();
    assert!(left.starts_with(&to) && left.is_file(), "{c:?}");
    left_with_its_proof(&fx, &before, &to, &e, &left, "holds it open");
    the_next_start_removes_the_rest(&fx, &before, &to, &Facts::all_clear());
}

/// The removal of an old copy names every file of the launcher's own that it could not remove (lock files, markers)
/// and every folder that is empty and could not be removed, with the OS error; a later call takes them.
#[test]
#[cfg(unix)] // a read-only folder
fn the_old_copys_removal_names_what_it_could_not_remove() {
    let fx = Fixture::build("old-named");
    let to = fx.base.join("moved");
    let e = mv(&fx, &to, &linking(), &inject(Inject::StopAfter(Step::Activate))).unwrap_err();
    assert_eq!(e.code, ErrorCode::Internal);
    let old = fx.dirs.root.clone();
    let locks = [old.join("quick.lock"), old.join(df::LOCK)];
    assert!(locks.iter().all(|l| l.is_file()), "the move's locks were taken in the old folder");
    std::fs::set_permissions(&old, std::fs::Permissions::from_mode(0o555)).unwrap();
    let c = dfm::remove_old_copy(&fx.env, &mut NoProgress);
    std::fs::set_permissions(&old, std::fs::Permissions::from_mode(0o755)).unwrap();
    let c = c.unwrap();
    let why: BTreeMap<PathBuf, String> = c.kept.iter().map(|k| (k.path.clone(), k.reason.clone())).collect();
    for l in &locks {
        assert!(why.get(l).is_some_and(|r| r.contains("could not be removed") && r.contains("ermission denied")), "{} is named: {why:#?}", l.display());
    }
    assert!(why.get(&old.join("library")).is_some_and(|r| r.contains("empty folder") && r.contains("ermission denied")), "{why:#?}");
    assert!(!lib_dir(&fx.dirs, &fx.mods.kiln).exists(), "what could go went");
    // a later call takes them
    let c = dfm::remove_old_copy(&fx.env, &mut NoProgress).unwrap();
    assert!(c.done && !c.kept.iter().any(|k| locks.contains(&k.path) || k.path == old.join("library")), "{c:?}");
    assert!(locks.iter().all(|l| !l.exists()) && !old.join("library").exists());
}

/// A file gone from the source when the copy is checked again: its copy goes too, and when it cannot (its folder in the
/// target is read-only, as its source folder now is) the move fails naming it and keeps its proof.
#[test]
#[cfg(unix)] // a read-only folder
fn a_copy_of_a_file_gone_from_the_source_that_cannot_be_removed_stays_named() {
    /// Removes `file` from the source once it is copied and makes its folder read-only (the copy's folder gets that
    /// mode from it).
    struct Gone {
        rel: String,
        file: PathBuf,
        done: bool,
    }
    impl Progress for Gone {
        fn report(&mut self, _: Option<f32>, m: &str) {
            if !self.done && m.strip_prefix("copying ") == Some(self.rel.as_str()) {
                std::fs::remove_file(&self.file).unwrap();
                std::fs::set_permissions(self.file.parent().unwrap(), std::fs::Permissions::from_mode(0o555)).unwrap();
                self.done = true;
            }
        }
    }
    let fx = Fixture::build("gone-held");
    let mut want = tree(&fx.dirs.root);
    let rel = want.keys().find(|k| k.starts_with("My Notes/")).cloned().expect("a file the launcher does not know");
    want.remove(&rel);
    let to = fx.base.join("target");
    let mut p = Gone { rel: rel.clone(), file: fx.dirs.root.join(&rel), done: false };
    let e = dfm::move_to(&located(&fx.env), &fx.env, &to, &Facts::all_clear(), None, &MoveOptions::default(), &mut p).unwrap_err();
    assert!(p.done);
    assert!(e.message.contains("to copy it again"), "{e:?}");
    left_with_its_proof(&fx, &want, &to, &e, &to.join(&rel), "ermission denied");
    for d in [fx.dirs.root.join("My Notes"), to.join("My Notes")] {
        std::fs::set_permissions(d, std::fs::Permissions::from_mode(0o755)).unwrap();
    }
    // the next start: the copy of the file gone from the old folder has no proof there any more, so it stays, named;
    // once the player takes it out the rest goes
    let c = dfm::remove_partial_copy(&fx.env).unwrap();
    assert!(!c.done && c.kept.iter().any(|k| k.path == to.join(&rel) && k.reason == dfm::partial_reason::NO_SUCH_FILE), "{c:?}");
    std::fs::remove_file(to.join(&rel)).unwrap();
    the_next_start_removes_the_rest(&fx, &want, &to, &Facts::all_clear());
}

// ------------------------------------------------------------------------------------------------ core-6

/// A move stopped as if the process died after the copy (`Step::Copy`): the old place in use, the partial copy at
/// `target` with this move's marker. `backups/` is one of the journal's entries.
fn crashed_after_copy(tag: &str, facts: &Facts) -> (Fixture, BTreeMap<String, String>, PathBuf) {
    let fx = Fixture::build(tag);
    put(&fx.dirs.root, "backups/install-20261101T000000Z/note.txt", b"a backup the player made");
    let before = tree(&fx.dirs.root);
    let to = fx.base.join("target");
    let e = mv(&fx, &to, facts, &inject(Inject::StopAfter(Step::Copy))).unwrap_err();
    assert_eq!(e.code, ErrorCode::Internal);
    let Pending::Uncommitted { journal } = dfm::pending(&fx.env).unwrap() else { panic!("{:?}", dfm::pending(&fx.env)) };
    assert!(journal.entries.iter().any(|e| e.name == "backups"), "{:?}", journal.entries);
    assert!(to.join(df::MOVE_MARKER).is_file());
    (fx, before, to)
}

/// What a recovery kept, by path: the reason.
fn kept_of(c: &dfm::Cleanup) -> BTreeMap<PathBuf, String> {
    c.kept.iter().map(|k| (k.path.clone(), k.reason.clone())).collect()
}

/// A recovery that kept something: the marker and the journal (with the result) stay, the move is still unfinished.
fn still_unfinished(fx: &Fixture, to: &Path, c: &dfm::Cleanup) {
    assert!(!c.done && !c.folder_removed, "{c:?}");
    assert!(to.join(df::MOVE_MARKER).is_file(), "the marker stays");
    let Pending::Uncommitted { journal } = dfm::pending(&fx.env).unwrap() else { panic!("the journal stays: {:?}", dfm::pending(&fx.env)) };
    assert_eq!(journal.result.as_ref().map(|r| r.kept.len()), Some(c.kept.len()), "the journal holds the result");
    assert_eq!(located(&fx.env).dirs.root, fx.dirs.root, "the old place is in use");
}

/// The next call once the player took the named files out: the rest goes, with the target and the journal.
fn finished(fx: &Fixture, to: &Path) {
    let c = dfm::remove_partial_copy(&fx.env).unwrap();
    assert!(c.done && c.kept.is_empty() && c.folder_removed, "{c:?}");
    assert!(!to.exists(), "{:?}", std::fs::read_dir(to).map(|d| d.flatten().map(|e| e.file_name()).collect::<Vec<_>>()));
    assert_eq!(dfm::pending(&fx.env).unwrap(), Pending::Nothing);
}

/// The verifier's case: a file added to the target below a journal entry after the crash is not the move's. Recovery
/// leaves it as it is and names it, removes what the old folder proves, keeps the marker and the journal; once the file
/// is taken out, the next call removes everything.
#[test]
fn recovery_keeps_a_file_added_to_the_partial_copy_after_a_crash() {
    let (fx, before, to) = crashed_after_copy("added-after-crash", &linking());
    let added = to.join("backups/added-after-crash.bin");
    std::fs::write(&added, b"bytes the move never made").unwrap();
    let c = dfm::remove_partial_copy(&fx.env).unwrap();
    assert_eq!(std::fs::read(&added).unwrap(), b"bytes the move never made", "the file is there and unchanged");
    assert_eq!(kept_of(&c), BTreeMap::from([(added.clone(), dfm::partial_reason::NO_SUCH_FILE.to_string())]), "{c:?}");
    assert!(c.removed_files > 0 && !to.join("library").exists() && !to.join("backups/install-20261101T000000Z").exists(), "what the old folder proves went: {c:?}");
    still_unfinished(&fx, &to, &c);
    assert_eq!(tree(&fx.dirs.root), before);
    std::fs::remove_file(&added).unwrap();
    finished(&fx, &to);
    old_place_intact(&fx, &before, &to);
}

/// The hash decides, not the size or the time: a target file changed after the crash, a target file replaced by
/// another of the same size and time, and a target file whose source changed after the crash (same size and time)
/// each stay, named. By copies (a hard link is one file with the old folder's).
#[test]
fn recovery_keeps_what_differs_from_the_old_folder() {
    use std::io::Write;
    let (fx, _, to) = crashed_after_copy("differs", &Facts::all_clear());
    let files: Vec<String> = tree(&fx.dirs.root).keys().filter(|k| k.starts_with("library/")).cloned().collect();
    let pick: Vec<&String> = files.iter().filter(|k| std::fs::metadata(fx.dirs.root.join(k)).unwrap().len() >= 4).take(3).collect();
    assert_eq!(pick.len(), 3);
    let same_size_other = |p: &Path| {
        let m = std::fs::metadata(p).unwrap();
        let mut b = std::fs::read(p).unwrap();
        for x in b.iter_mut() {
            *x = !*x;
        }
        std::fs::write(p, &b).unwrap();
        std::fs::File::options().write(true).open(p).unwrap().set_modified(m.modified().unwrap()).unwrap();
        assert_eq!(std::fs::metadata(p).unwrap().len(), m.len());
    };
    let changed = to.join(pick[0]);
    std::fs::OpenOptions::new().append(true).open(&changed).unwrap().write_all(b" and more").unwrap();
    let replaced = to.join(pick[1]);
    let tmp = to.join("replacement.tmp-test");
    std::fs::copy(&replaced, &tmp).unwrap();
    same_size_other(&tmp);
    std::fs::rename(&tmp, &replaced).unwrap();
    let source_changed = to.join(pick[2]);
    same_size_other(&fx.dirs.root.join(pick[2]));
    let c = dfm::remove_partial_copy(&fx.env).unwrap();
    let kept = kept_of(&c);
    for p in [&changed, &replaced, &source_changed] {
        assert_eq!(kept.get(p).map(String::as_str), Some(dfm::partial_reason::DIFFERS), "{} stays: {kept:#?}", p.display());
        assert!(p.is_file());
    }
    assert_eq!(kept.len(), 3, "{kept:#?}");
    still_unfinished(&fx, &to, &c);
    for p in [&changed, &replaced, &source_changed] {
        std::fs::remove_file(p).unwrap();
    }
    finished(&fx, &to);
}

/// Links in the partial copy are never followed and never removed, whatever they point to; a source path that is a
/// link (the file itself, or a folder on the way) is no proof.
#[test]
#[cfg(unix)] // symlinks
fn recovery_never_follows_a_link() {
    let (fx, _, to) = crashed_after_copy("links", &Facts::all_clear());
    // links placed in the target, to a file and to a folder outside it
    let outside = fx.base.join("outside");
    put(&outside, "dir/inner.txt", b"not the move's");
    put(&outside, "file.txt", b"not the move's either");
    let to_file = to.join("library/link-to-file");
    let to_dir = to.join("library/link-to-folder");
    symlink(outside.join("file.txt"), &to_file).unwrap();
    symlink(outside.join("dir"), &to_dir).unwrap();
    // in the old folder: a file replaced by a link to an identical file, and a folder replaced by a link to an identical
    // copy of it
    let files: Vec<String> = tree(&fx.dirs.root).keys().filter(|k| k.starts_with("library/")).cloned().collect();
    let linked_file = files[0].clone();
    let real = fx.base.join("elsewhere-file");
    std::fs::copy(fx.dirs.root.join(&linked_file), &real).unwrap();
    std::fs::remove_file(fx.dirs.root.join(&linked_file)).unwrap();
    symlink(&real, fx.dirs.root.join(&linked_file)).unwrap();
    let in_dir = files.iter().find(|k| !k.starts_with(&format!("{}/", Path::new(&linked_file).parent().unwrap().to_str().unwrap()))).unwrap().clone();
    let dir_rel = Path::new(&in_dir).parent().unwrap().to_str().unwrap().to_string();
    let elsewhere = fx.base.join("elsewhere-dir");
    std::fs::rename(fx.dirs.root.join(&dir_rel), &elsewhere).unwrap();
    symlink(&elsewhere, fx.dirs.root.join(&dir_rel)).unwrap();
    let c = dfm::remove_partial_copy(&fx.env).unwrap();
    let kept = kept_of(&c);
    assert_eq!(kept.get(&to_file).map(String::as_str), Some(dfm::partial_reason::LINK), "{kept:#?}");
    assert_eq!(kept.get(&to_dir).map(String::as_str), Some(dfm::partial_reason::LINK), "{kept:#?}");
    assert!(std::fs::symlink_metadata(&to_file).unwrap().file_type().is_symlink() && std::fs::symlink_metadata(&to_dir).unwrap().file_type().is_symlink());
    assert_eq!(std::fs::read(outside.join("dir/inner.txt")).unwrap(), b"not the move's", "what a link points to is untouched");
    assert_eq!(std::fs::read(outside.join("file.txt")).unwrap(), b"not the move's either");
    assert_eq!(kept.get(&to.join(&linked_file)).map(String::as_str), Some(dfm::partial_reason::NO_SUCH_FILE), "a link in the old folder proves nothing: {kept:#?}");
    assert_eq!(kept.get(&to.join(&in_dir)).map(String::as_str), Some(dfm::partial_reason::NO_SUCH_FILE), "nor a folder on the way that is a link: {kept:#?}");
    assert!(to.join(&linked_file).is_file() && to.join(&in_dir).is_file());
    still_unfinished(&fx, &to, &c);
}

/// A top-level entry the journal does not list stays and is named (the target stays); an empty folder the old folder
/// does not have stays and is named; an empty folder the old folder has goes.
#[test]
fn recovery_keeps_entries_the_journal_does_not_list_and_folders_the_old_folder_lacks() {
    let (fx, _, to) = crashed_after_copy("unlisted", &linking());
    put(&to, "Player Stuff/keep.txt", b"mine");
    put(&to, "notes.txt", b"mine too");
    let empty_new = to.join("library/added-empty");
    std::fs::create_dir_all(&empty_new).unwrap();
    let c = dfm::remove_partial_copy(&fx.env).unwrap();
    let kept = kept_of(&c);
    assert_eq!(kept.get(&to.join("Player Stuff")).map(String::as_str), Some(dfm::partial_reason::NOT_COPIED), "{kept:#?}");
    assert_eq!(kept.get(&to.join("notes.txt")).map(String::as_str), Some(dfm::partial_reason::NOT_COPIED), "{kept:#?}");
    assert_eq!(kept.get(&empty_new).map(String::as_str), Some(dfm::partial_reason::NO_SUCH_FOLDER), "{kept:#?}");
    assert_eq!(kept.len(), 3, "{kept:#?}");
    assert_eq!(c.kept.iter().find(|k| k.path == to.join("Player Stuff")).unwrap().bytes, 4);
    assert!(to.join("Player Stuff/keep.txt").is_file() && to.join("notes.txt").is_file() && empty_new.is_dir());
    assert!(!to.join("profiles").exists(), "what the old folder proves went");
    still_unfinished(&fx, &to, &c);
    std::fs::remove_dir_all(to.join("Player Stuff")).unwrap();
    std::fs::remove_file(to.join("notes.txt")).unwrap();
    std::fs::remove_dir(&empty_new).unwrap();
    finished(&fx, &to);
}

/// A clean partial copy goes in full, with the target and the journal: by hard links (the same file is proof without
/// reading it: here one the owner cannot read), by copies, each with the server record the move rewrote in it.
#[test]
fn recovery_removes_a_clean_partial_copy_in_full() {
    for (tag, facts) in [("clean-links", linking()), ("clean-copies", Facts::all_clear())] {
        let (fx, before, to) = crashed_after_copy(tag, &facts);
        let record = "servers/isles.json";
        assert_ne!(std::fs::read(to.join(record)).unwrap(), std::fs::read(fx.dirs.root.join(record)).unwrap(), "{tag}: the copy's backupDir names the new place");
        #[cfg(unix)]
        let unreadable = (tag == "clean-links").then(|| {
            let f = lib_dir(&fx.dirs, &fx.mods.native).join("fps.dll");
            assert_eq!(std::fs::metadata(&f).unwrap().ino(), std::fs::metadata(to.join(f.strip_prefix(&fx.dirs.root).unwrap())).unwrap().ino());
            std::fs::set_permissions(&f, std::fs::Permissions::from_mode(0o000)).unwrap();
            f
        });
        let c = dfm::remove_partial_copy(&fx.env);
        #[cfg(unix)]
        if let Some(f) = &unreadable {
            std::fs::set_permissions(f, std::fs::Permissions::from_mode(0o644)).unwrap();
        }
        let c = c.unwrap();
        assert!(c.done && c.kept.is_empty() && c.folder_removed && c.removed_files as usize == before.len(), "{tag}: every file the old folder has: {c:?}");
        old_place_intact(&fx, &before, &to);
    }
}

/// Counts the polls of `canceled()` and cancels from the `after`-th on; keeps the reports.
struct CancelAt {
    after: u32,
    polls: std::cell::Cell<u32>,
    reports: Vec<(Option<f32>, String)>,
}
impl Progress for CancelAt {
    fn report(&mut self, f: Option<f32>, m: &str) {
        self.reports.push((f, m.to_string()));
    }
    fn canceled(&self) -> bool {
        self.polls.set(self.polls.get() + 1);
        self.polls.get() > self.after
    }
}

/// A cancel halfway: what went is gone, the rest, the marker and the journal stay (`interrupted`); the next call
/// finishes. The reports carry a fraction and the file checked.
#[test]
fn recovery_can_be_canceled_and_goes_on() {
    let (fx, before, to) = crashed_after_copy("cancel-recovery", &Facts::all_clear());
    let mut p = CancelAt { after: 10, polls: std::cell::Cell::new(0), reports: vec![] };
    let c = dfm::remove_partial_copy_with(&fx.env, &mut p).unwrap();
    assert!(c.interrupted && !c.done && c.removed_files == 10 && c.kept.is_empty(), "{c:?}");
    assert!(to.join(df::MOVE_MARKER).is_file());
    let Pending::Uncommitted { journal } = dfm::pending(&fx.env).unwrap() else { panic!() };
    assert!(journal.result.is_some_and(|r| r.interrupted));
    assert!(p.reports.iter().any(|(f, m)| f.is_some_and(|f| f > 0.0 && f < 1.0) && m.starts_with("checking ")), "{:?}", p.reports);
    let c = dfm::remove_partial_copy_with(&fx.env, &mut NoProgress).unwrap();
    assert!(c.done && !c.interrupted && c.removed_files > 0 && c.folder_removed, "{c:?}");
    old_place_intact(&fx, &before, &to);
}

/// Leftovers of the move's own marker writes at the target's top go when their content shows they are this move's
/// (a move marker with this journal's id, a folder marker created after the move started); any other stays, named.
#[test]
fn recovery_removes_marker_leftovers_only_by_their_content() {
    let (fx, before, to) = crashed_after_copy("marker-tmp", &linking());
    let j = dfm::read_journal(&fx.env).unwrap().unwrap();
    let move_tmp = to.join(format!("{}.tmp", df::MOVE_MARKER));
    let folder_tmp = to.join(format!("{}.tmp", df::MARKER));
    std::fs::write(&move_tmp, serde_json::to_vec(&serde_json::json!({"format": 1, "id": "another move"})).unwrap()).unwrap();
    std::fs::write(&folder_tmp, b"{\"format\": 1, \"crea").unwrap(); // cut off
    let c = dfm::remove_partial_copy(&fx.env).unwrap();
    assert_eq!(
        kept_of(&c),
        BTreeMap::from([(move_tmp.clone(), dfm::partial_reason::NOT_THIS_MOVE.to_string()), (folder_tmp.clone(), dfm::partial_reason::NOT_THIS_MOVE.to_string())]),
        "{c:?}"
    );
    still_unfinished(&fx, &to, &c);
    std::fs::write(&move_tmp, serde_json::to_vec(&serde_json::json!({"format": 1, "id": j.id})).unwrap()).unwrap();
    std::fs::write(&folder_tmp, serde_json::to_vec(&serde_json::json!({"format": 1, "created": util::now_rfc3339()})).unwrap()).unwrap();
    finished(&fx, &to);
    old_place_intact(&fx, &before, &to);
}

/// After a recovery that kept something, the player cleans up by hand and the next start finishes cleanly: (a) the
/// kept files taken out: the rest, the marker, the target and the journal go; (b) the whole target deleted: the journal
/// and the file record go; (c) the target emptied completely, marker included: nothing is left that the launcher
/// could remove wrongly (also when only the lock file the move made is left), so the folder goes when the move made it
/// (one that was there before stays) and the records go.
#[test]
fn a_target_the_player_cleaned_up_by_hand_finishes_cleanly() {
    for case in ["kept-taken-out", "target-deleted", "target-emptied", "emptied-but-the-lock", "existing-target-emptied"] {
        let fx = Fixture::build(&format!("by-hand-{case}"));
        let before = tree(&fx.dirs.root);
        let to = fx.base.join("target");
        if case == "existing-target-emptied" {
            std::fs::create_dir_all(&to).unwrap(); // the player picked an empty folder
        }
        mv(&fx, &to, &linking(), &inject(Inject::StopAfter(Step::Copy))).unwrap_err();
        let added = to.join("library/added.bin");
        std::fs::write(&added, b"not the move's").unwrap();
        let c = dfm::remove_partial_copy(&fx.env).unwrap();
        still_unfinished(&fx, &to, &c);
        match case {
            "kept-taken-out" => std::fs::remove_file(&added).unwrap(),
            "target-deleted" => std::fs::remove_dir_all(&to).unwrap(),
            _ => {
                for e in std::fs::read_dir(&to).unwrap().flatten() {
                    let p = e.path();
                    if case == "emptied-but-the-lock" && e.file_name() == df::LOCK {
                        continue; // the lock file the move made
                    }
                    if p.is_dir() {
                        std::fs::remove_dir_all(&p).unwrap()
                    } else {
                        std::fs::remove_file(&p).unwrap()
                    }
                }
            }
        }
        let c = dfm::remove_partial_copy(&fx.env).unwrap();
        assert!(c.done && c.kept.is_empty(), "{case}: {c:?}");
        assert_eq!(dfm::pending(&fx.env).unwrap(), Pending::Nothing, "{case}: the journal and the file record are gone");
        assert!(!fx.dirs.root.join(df::JOURNAL).exists() && !fx.dirs.root.join(df::FILE_RECORD).exists());
        if case == "existing-target-emptied" {
            assert!(to.is_dir() && std::fs::read_dir(&to).unwrap().next().is_none() && !c.folder_removed, "{case}: the folder that was there before stays");
        } else {
            assert!(!to.exists() && c.folder_removed, "{case}: {c:?}");
        }
        assert_eq!(tree(&fx.dirs.root), before, "{case}");
        assert_eq!(located(&fx.env).dirs.root, fx.dirs.root);
    }
}

/// `servers/<id>.json`, which the move rewrites: its copy goes while it is exactly what the move writes from the old
/// folder's file; when the old folder's file changed after the move stopped, the copy no longer is, so it stays, named.
#[test]
fn a_server_record_goes_only_while_it_is_the_rewrite_of_the_old_folders() {
    for changed in [false, true] {
        let (fx, _, to) = crashed_after_copy(&format!("server-record-{changed}"), &Facts::all_clear());
        let record = "servers/isles.json";
        let copy = to.join(record);
        assert_ne!(std::fs::read(&copy).unwrap(), std::fs::read(fx.dirs.root.join(record)).unwrap(), "the copy's backupDir names the new place");
        if changed {
            let mut v: serde_json::Value = serde_json::from_slice(&std::fs::read(fx.dirs.root.join(record)).unwrap()).unwrap();
            v["stopTimeoutS"] = serde_json::json!(121);
            std::fs::write(fx.dirs.root.join(record), serde_json::to_string_pretty(&v).unwrap()).unwrap();
        }
        let c = dfm::remove_partial_copy(&fx.env).unwrap();
        if changed {
            assert_eq!(kept_of(&c), BTreeMap::from([(copy.clone(), dfm::partial_reason::DIFFERS.to_string())]), "{c:?}");
            assert!(copy.is_file());
            still_unfinished(&fx, &to, &c);
            std::fs::remove_file(&copy).unwrap();
            finished(&fx, &to);
        } else {
            assert!(c.done && c.kept.is_empty() && !to.exists(), "{c:?}");
        }
    }
}

/// A move back to the default root that stopped after the copy: a clean partial copy there goes by proof and leaves
/// the default root with its pointer; and when the player removed the partial copy by hand, marker included (the
/// default root keeps its pointer, the journal and the file record), the next start finishes cleanly too.
#[test]
fn a_partial_copy_at_the_default_root_finishes_cleanly() {
    for by_hand in [false, true] {
        let s = scratch(&format!("partial-default-{by_hand}"));
        let env = env_at(&s.0);
        let default = env.default_root().unwrap();
        put(&default, "library/kiln/a/1.0.0+00000000/M.dll", b"mod");
        put(&default, "settings.json", b"{}\n");
        let away = s.0.join("away");
        dfm::move_to(&env.locate().unwrap(), &env, &away, &Facts::all_clear(), None, &MoveOptions::default(), &mut NoProgress).unwrap();
        dfm::acknowledge(&env).unwrap();
        let before = tree(&away);
        let e = dfm::move_to(&env.locate().unwrap(), &env, &default, &Facts::all_clear(), None, &inject(Inject::StopAfter(Step::Copy)), &mut NoProgress).unwrap_err();
        assert_eq!(e.code, ErrorCode::Internal);
        let Pending::Uncommitted { journal } = dfm::pending(&env).unwrap() else { panic!("{:?}", dfm::pending(&env)) };
        assert!(default.join(df::MOVE_MARKER).is_file() && default.join("library").is_dir());
        if by_hand {
            for name in journal.entries.iter().map(|e| e.name.as_str()).chain([df::MOVE_MARKER]) {
                let p = default.join(name);
                if p.is_dir() {
                    std::fs::remove_dir_all(&p).unwrap();
                } else {
                    std::fs::remove_file(&p).unwrap();
                }
            }
        }
        let c = dfm::remove_partial_copy(&env).unwrap();
        assert!(c.done && c.kept.is_empty() && !c.folder_removed, "by hand {by_hand}: {c:?}");
        assert_eq!(dfm::pending(&env).unwrap(), Pending::Nothing);
        assert!(default.join(df::POINTER).is_file() && !default.join("library").exists() && !default.join(df::MOVE_MARKER).exists());
        assert_eq!(env.locate().unwrap().dirs.root, away);
        assert_eq!(tree(&away), before, "the folder in use is untouched");
    }
}
