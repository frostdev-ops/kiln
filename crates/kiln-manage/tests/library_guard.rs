//! Issue #4: the launcher must not remove a library version while a game may be reading it, and "Clean up" must never
//! offer a version the game's active profile file still points at. Temp folders; the process lookup is injected except
//! in the last test, which starts a real process named like the game.

use kiln_manage::game::{self, GameInstall, GameKind, PathMode, Platform};
use kiln_manage::library::{self, LibraryRef};
use kiln_manage::paths::DataDirs;
use kiln_manage::proc::ProcessInfo;
use kiln_manage::profile::{self, ProfileMod};
use kiln_manage::task::NoProgress;
use kiln_manage::{ErrorCode, Result};
use std::path::{Path, PathBuf};

struct Tmp(PathBuf);
impl Drop for Tmp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
fn tmp(tag: &str) -> Tmp {
    let p = std::env::temp_dir().join(format!("km-libguard-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&p);
    std::fs::create_dir_all(&p).unwrap();
    Tmp(p.canonicalize().unwrap())
}
fn put(root: &Path, rel: &str, data: &[u8]) {
    let p = root.join(rel);
    std::fs::create_dir_all(p.parent().unwrap()).unwrap();
    std::fs::write(p, data).unwrap();
}

/// Imports a Kiln mod whose content (so hash) depends on `content`.
fn import(dirs: &DataDirs, root: &Path, id: &str, version: &str, content: &str) -> LibraryRef {
    let d = root.join(format!("src/{id}-{version}-{content}"));
    put(&d, "kiln.mod.json", format!(r#"{{ "id": "{id}", "version": "{version}", "apiVersion": "0.2", "entryAssembly": "M.dll", "side": "both" }}"#).as_bytes());
    put(&d, "M.dll", content.as_bytes());
    let r = library::import(dirs, std::slice::from_ref(&d), &mut NoProgress).unwrap();
    r.imported.iter().chain(&r.existing).find(|x| x.id == id && x.version == version).cloned().unwrap()
}

fn install(path: &Path, name: &str, kind: GameKind, mode: PathMode) -> GameInstall {
    std::fs::create_dir_all(path.join("kiln")).unwrap();
    let exe = if kind == GameKind::Server { game::SERVER_EXE } else { game::CLIENT_EXE };
    GameInstall {
        id: format!("g-{name}"), kind, path: path.to_path_buf(), exe: exe.into(), app_id: kiln_manage::CLIENT_APP_ID, library: None,
        platform: Platform::Proton, prefix: None, build_id: None, exe_sha256: None, steam_flavor: None, path_mode: mode, source: "manual".into(),
    }
}

fn running(pid: u32, name: &str) -> ProcessInfo {
    ProcessInfo { pid, name: name.into(), exe: None, cmdline: vec![] }
}

/// `find` that reports `exe` running in `dir` only.
fn only(dir: &Path, exe: &'static str, p: ProcessInfo) -> impl FnMut(&Path, &str) -> Result<Option<ProcessInfo>> {
    let dir = dir.to_path_buf();
    move |d, e| Ok((d == dir && e == exe).then(|| p.clone()))
}

fn nothing(_: &Path, _: &str) -> Result<Option<ProcessInfo>> {
    Ok(None)
}

fn write_profile_file(game: &Path, rel: &str, id: &str, entries: serde_json::Value) {
    let text = serde_json::json!({ "format": 1, "id": id, "generation": 3, "mods": { "kiln": entries } });
    put(game, rel, serde_json::to_string_pretty(&text).unwrap().as_bytes());
}

#[test]
fn removal_refuses_while_the_game_runs_and_force_does_not_change_that() {
    let t = tmp("running");
    let dirs = DataDirs::at(t.0.join("data"));
    let r = import(&dirs, &t.0, "new-items", "1.0.0", "a");
    let folder = library::path_of(&dirs, &r).unwrap();
    let game_dir = t.0.join("Enshrouded");
    game::write_record(&dirs, &install(&game_dir, "client", GameKind::Client, PathMode::Z), None).unwrap();

    for force in [false, true] {
        let e = library::remove_with(&dirs, &r, force, &mut only(&game_dir, game::CLIENT_EXE, running(4242, "enshrouded.exe"))).unwrap_err();
        assert_eq!(e.code, ErrorCode::GameRunning, "force={force}");
        assert!(e.message.starts_with("Close Enshrouded first"), "{}", e.message);
        assert!(folder.is_dir(), "nothing was removed (force={force})");
    }
    // the same call once the game is closed
    library::remove_with(&dirs, &r, false, &mut nothing).unwrap();
    assert!(!folder.exists());
}

#[test]
fn a_running_server_blocks_removal_only_when_its_profile_file_points_at_the_library() {
    let t = tmp("server");
    let dirs = DataDirs::at(t.0.join("data"));
    let r = import(&dirs, &t.0, "new-items", "1.0.0", "a");
    let other = import(&dirs, &t.0, "new-items", "1.1.0", "b");
    let z = |x: &LibraryRef| kiln_manage::winpath::unix_to_z(&library::path_of(&dirs, x).unwrap()).unwrap();
    // a server folder the launcher knows only through its server record, with its mods synced in as copies
    let srv = t.0.join("EnshroudedServer");
    put(&srv, "kiln/mods/new-items/.kiln-source.json", b"{}");
    put(&t.0, "data/servers/home.json", format!(r#"{{ "id": "home", "dir": {:?} }}"#, srv.display().to_string()).as_bytes());
    let server = || only(&srv, game::SERVER_EXE, running(77, "enshrouded_serv"));
    // copies only: the running server does not read the library, removal goes ahead (the user's always-on server)
    library::remove_with(&dirs, &other, false, &mut server()).unwrap();
    let other = import(&dirs, &t.0, "new-items", "1.1.0", "b");

    // a profile activated on the server in Z mode points at r: refused, force or not
    write_profile_file(&srv, "kiln/profile.json", "srv", serde_json::json!([{ "id": "new-items", "path": z(&r) }]));
    let e = library::remove_with(&dirs, &r, true, &mut server()).unwrap_err();
    assert_eq!(e.code, ErrorCode::GameRunning);
    assert!(e.message.starts_with("Stop the Enshrouded server first"), "{}", e.message);
    // ... another version of the mod is not read by it (it is free to go, force because the file lists the mod id)
    library::remove_with(&dirs, &other, true, &mut server()).unwrap();
    // a copy-mode file (game-side relative paths) does not read the library either
    write_profile_file(&srv, "kiln/profile.json", "srv", serde_json::json!([
        { "id": "new-items", "version": r.version, "hash": r.hash, "path": "profiles/srv/kiln/new-items" }]));
    library::remove_with(&dirs, &r, true, &mut server()).unwrap();
    let r = import(&dirs, &t.0, "new-items", "1.0.0", "a");
    // a file that cannot be read, or an entry that cannot be mapped to the host, may point anywhere: refused
    put(&srv, "kiln/profile.json", b"{ not json");
    assert_eq!(library::remove_with(&dirs, &r, true, &mut server()).unwrap_err().code, ErrorCode::GameRunning);
    if cfg!(not(windows)) {
        // a UNC path has no host path under Wine
        write_profile_file(&srv, "kiln/profile.json", "srv", serde_json::json!([{ "id": "x", "path": "\\\\host\\share\\x" }]));
        assert_eq!(library::remove_with(&dirs, &r, true, &mut server()).unwrap_err().code, ErrorCode::GameRunning);
    }
    // play-once file too
    std::fs::remove_file(srv.join("kiln/profile.json")).unwrap();
    write_profile_file(&srv, "kiln/profile.once.json", "srv", serde_json::json!([{ "id": "new-items", "path": z(&r) }]));
    assert_eq!(library::remove_with(&dirs, &r, true, &mut server()).unwrap_err().code, ErrorCode::GameRunning);

    // a server installed like a game (an install record) follows the same rule; a client there always refuses
    let t2 = tmp("server-install");
    let dirs2 = DataDirs::at(t2.0.join("data"));
    let r2 = import(&dirs2, &t2.0, "new-items", "1.0.0", "a");
    let srv2 = t2.0.join("srv");
    game::write_record(&dirs2, &install(&srv2, "srv", GameKind::Server, PathMode::Copy), None).unwrap();
    let client = t2.0.join("Enshrouded");
    game::write_record(&dirs2, &install(&client, "client", GameKind::Client, PathMode::Copy), None).unwrap();
    let e = library::remove_with(&dirs2, &r2, false, &mut only(&client, game::CLIENT_EXE, running(79, "enshrouded.exe"))).unwrap_err();
    assert_eq!(e.code, ErrorCode::GameRunning, "a running client refuses even with copies only");
    assert!(library::path_of(&dirs2, &r2).unwrap().is_dir());
    library::remove_with(&dirs2, &r2, false, &mut only(&srv2, game::SERVER_EXE, running(78, "enshrouded_serv"))).unwrap();
}

#[test]
fn a_process_lookup_that_fails_refuses_instead_of_guessing() {
    let t = tmp("lookup");
    let dirs = DataDirs::at(t.0.join("data"));
    let r = import(&dirs, &t.0, "new-items", "1.0.0", "a");
    game::write_record(&dirs, &install(&t.0.join("Enshrouded"), "client", GameKind::Client, PathMode::Z), None).unwrap();
    let e = library::remove_with(&dirs, &r, true, &mut |_, _| Err(kiln_manage::Error::invalid("cannot inspect processes"))).unwrap_err();
    assert_eq!(e.message, "cannot inspect processes");
    assert!(library::path_of(&dirs, &r).unwrap().is_dir());
}

#[test]
fn users_counts_the_files_the_game_holds() {
    let t = tmp("users");
    let dirs = DataDirs::at(t.0.join("data"));
    let v1 = import(&dirs, &t.0, "new-items", "1.0.0", "a");
    let v2 = import(&dirs, &t.0, "new-items", "1.1.0", "b");
    let other = import(&dirs, &t.0, "other-mod", "1.0.0", "c");
    let game_dir = t.0.join("Enshrouded");
    game::write_record(&dirs, &install(&game_dir, "client", GameKind::Client, PathMode::Z), None).unwrap();
    assert!(library::users(&dirs, &v1).is_empty());

    // The game's active profile.json points at v1 by its Z: path (no version or hash in the entry: older files)
    let z = |r: &LibraryRef| kiln_manage::winpath::unix_to_z(&library::path_of(&dirs, r).unwrap()).unwrap();
    write_profile_file(&game_dir, "kiln/profile.json", "work", serde_json::json!([{ "id": "new-items", "path": z(&v1) }]));
    assert_eq!(library::users(&dirs, &v1), ["work (active in Enshrouded)"]);
    assert!(library::users(&dirs, &v2).is_empty(), "another version of the mod is not used");
    assert!(library::users(&dirs, &other).is_empty());

    // profile.once.json: a start queued for one launch; a native-form (host) path counts the same
    let host = library::path_of(&dirs, &v2).unwrap().display().to_string();
    write_profile_file(&game_dir, "kiln/profile.once.json", "tryout", serde_json::json!([{ "id": "new-items", "path": host }]));
    assert_eq!(library::users(&dirs, &v2), ["tryout (play-once in Enshrouded)"]);

    // Clean up never lists them; the unused one stays listed
    let unused = library::unused(&dirs).unwrap();
    assert_eq!(unused, vec![other.clone()]);

    // removal refuses without force, and succeeds with force once the game is closed
    let e = library::remove_with(&dirs, &v1, false, &mut nothing).unwrap_err();
    assert_eq!(e.code, ErrorCode::Conflict);
    assert!(e.message.contains("work (active in Enshrouded)"), "{}", e.message);
    library::remove_with(&dirs, &v1, true, &mut nothing).unwrap();
    library::remove_with(&dirs, &other, false, &mut nothing).unwrap();
}

#[test]
fn a_profile_that_lists_it_and_is_active_is_named_once() {
    let t = tmp("dedupe");
    let dirs = DataDirs::at(t.0.join("data"));
    let v1 = import(&dirs, &t.0, "new-items", "1.0.0", "a");
    let game_dir = t.0.join("Enshrouded");
    game::write_record(&dirs, &install(&game_dir, "client", GameKind::Client, PathMode::Copy), None).unwrap();
    let mut p = profile::create(&dirs, "Work", None).unwrap();
    p.mods.kiln.push(ProfileMod { id: v1.id.clone(), version: v1.version.clone(), hash: v1.hash.clone(), enabled: true });
    let p = profile::save(&dirs, &p).unwrap();
    // copy mode: the file carries a game-side relative path and the version + hash
    write_profile_file(&game_dir, "kiln/profile.json", &p.id, serde_json::json!([
        { "id": "new-items", "version": v1.version, "hash": v1.hash, "path": format!("profiles/{}/kiln/new-items", p.id) }]));
    assert_eq!(library::users(&dirs, &v1), [p.id.clone()]);
}

#[test]
fn a_stale_active_file_still_protects_a_version_no_profile_lists() {
    // The profile was edited after it was activated: meta.json no longer lists v1, the game's profile.json still does.
    let t = tmp("stale");
    let dirs = DataDirs::at(t.0.join("data"));
    let v1 = import(&dirs, &t.0, "new-items", "1.0.0", "a");
    let game_dir = t.0.join("Enshrouded");
    game::write_record(&dirs, &install(&game_dir, "client", GameKind::Client, PathMode::Z), None).unwrap();
    let p = profile::create(&dirs, "Work", None).unwrap(); // lists nothing
    write_profile_file(&game_dir, "kiln/profile.json", &p.id, serde_json::json!([
        { "id": "new-items", "version": v1.version, "hash": v1.hash, "path": kiln_manage::winpath::unix_to_z(&library::path_of(&dirs, &v1).unwrap()).unwrap() }]));
    assert_eq!(library::users(&dirs, &v1), [format!("{} (active in Enshrouded)", p.id)]);
    assert!(library::unused(&dirs).unwrap().is_empty());
}

#[cfg(target_os = "linux")]
#[test]
fn a_real_process_named_like_the_game_blocks_removal() {
    // The real lookup (kiln_manage::proc): Linux matches Wine's process by name and working folder.
    let sleep = ["/usr/bin/sleep", "/bin/sleep"].into_iter().map(PathBuf::from).find(|p| p.is_file());
    let Some(sleep) = sleep else { return };
    let t = tmp("real");
    let dirs = DataDirs::at(t.0.join("data"));
    let r = import(&dirs, &t.0, "new-items", "1.0.0", "a");
    let game_dir = t.0.join("Enshrouded");
    game::write_record(&dirs, &install(&game_dir, "client", GameKind::Client, PathMode::Z), None).unwrap();
    let exe = game_dir.join(game::CLIENT_EXE);
    std::fs::copy(sleep, &exe).unwrap();
    let mut child = std::process::Command::new(&exe).arg("60").current_dir(&game_dir).spawn().unwrap();
    // wait until the process lookup sees it (the kernel names it after the exe at exec)
    for _ in 0..100 {
        if kiln_manage::proc::find_exe(&game_dir, game::CLIENT_EXE).unwrap().is_some() { break; }
        std::thread::sleep(std::time::Duration::from_millis(40));
    }
    let blocked = library::remove(&dirs, &r, true).err();
    child.kill().unwrap();
    child.wait().unwrap();
    let e = blocked.expect("a running enshrouded.exe in the game folder blocks removal");
    assert_eq!(e.message, "Close Enshrouded first: the game may be reading this mod.");
    assert!(library::path_of(&dirs, &r).unwrap().is_dir());
    library::remove(&dirs, &r, false).unwrap();
}

#[cfg(unix)]
#[test]
fn a_symlinked_home_maps_both_ways() {
    // Fedora Atomic / Bazzite: /home -> /var/home. The launcher may know the data dir by either path, and Wine may
    // write the game-side path through either (`Z:\home\...` or `Z:\var\home\...`).
    let t = tmp("symhome");
    let real = t.0.join("var/home");
    std::fs::create_dir_all(&real).unwrap();
    let link = t.0.join("home");
    std::os::unix::fs::symlink(&real, &link).unwrap();
    let game_dir = real.join("Enshrouded");
    for (data_root, written_through) in [(&link, &real), (&real, &link)] {
        let dirs = DataDirs::at(data_root.join("data"));
        let r = import(&dirs, data_root, "new-items", "1.0.0", "a");
        game::write_record(&dirs, &install(&game_dir, "client", GameKind::Client, PathMode::Z), None).unwrap();
        let entry = library::path_of(&dirs, &r).unwrap();
        let rel = entry.strip_prefix(data_root).unwrap();
        let z = kiln_manage::winpath::unix_to_z(&written_through.join(rel)).unwrap();
        write_profile_file(&game_dir, "kiln/profile.json", "work", serde_json::json!([{ "id": "new-items", "path": z }]));
        assert_eq!(library::users(&dirs, &r), ["work (active in Enshrouded)"], "data at {}, Z: path {z}", data_root.display());
        assert!(library::unused(&dirs).unwrap().is_empty());
        let _ = std::fs::remove_dir_all(real.join("data"));
        let _ = std::fs::remove_dir_all(real.join("src"));
    }
}
