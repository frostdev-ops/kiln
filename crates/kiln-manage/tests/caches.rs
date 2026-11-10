//! The caches behind the launcher's polls (crate::filecache and its users) give what a fresh read gives: a bundle zip
//! replaced at the same path, an installed file edited back to its size and mtime, a proxy DLL swapped, hooks.json
//! rewritten, and the game process found, followed by pid, and lost.
#![cfg(feature = "zip")]

use kiln_manage::bundle::{self, CreateOptions};
use kiln_manage::game::{GameInstall, GameKind, PathMode, Platform};
use kiln_manage::install::{self, InstallOptions};
use kiln_manage::pe::{self, build_test_dll};
use kiln_manage::task::NoProgress;
use kiln_manage::{compat, paths::DataDirs};
use std::path::{Path, PathBuf};
use std::time::SystemTime;

struct Tmp(PathBuf);
impl Drop for Tmp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
fn tmp(tag: &str) -> Tmp {
    let p = std::env::temp_dir().join(format!("km-caches-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&p);
    std::fs::create_dir_all(&p).unwrap();
    Tmp(p.canonicalize().unwrap())
}

fn put(root: &Path, rel: &str, data: &[u8]) {
    let p = root.join(rel);
    std::fs::create_dir_all(p.parent().unwrap()).unwrap();
    std::fs::write(p, data).unwrap();
}

/// Writes `data` to `p` through a temp file and a rename (as every writer of these files does), then sets the old
/// mtime back: same size, same mtime, other bytes.
fn replace_keeping_mtime(p: &Path, data: &[u8]) {
    let t = std::fs::metadata(p).unwrap().modified().unwrap();
    let tmp = p.with_extension("swap");
    std::fs::write(&tmp, data).unwrap();
    std::fs::rename(&tmp, p).unwrap();
    std::fs::File::options().write(true).open(p).unwrap().set_modified(t).unwrap();
}

fn stage(root: &Path, tag: &str) -> PathBuf {
    let s = root.join(format!("stage-{tag}"));
    put(&s, "kiln-runtime.json", br#"{
  "format": 1, "kiln": "0.2.0-dev", "abi": 2, "api": "0.2.0", "dotnet": "10.0.14",
  "components": { "core": { "sides": ["server", "client"], "default": true, "description": "proxy, core, host" } },
  "builds": { "client": [] },
  "config": ["kiln/kiln.json"]
}"#);
    put(&s, "core/dinput8.dll", &build_test_dll(&["DirectInput8Create", "GetdfDIJoystick", "KilnProxyInfo"], format!("KilnProxy/2 0.2.0-dev+{tag}\0").as_bytes()));
    put(&s, "core/kiln/kiln-core.dll", format!("core {tag} ........").as_bytes());
    put(&s, "core/kiln/kiln.json", b"{\n  \"policy\": \"verified-only\"\n}\n");
    s
}

fn client(game: &Path) -> GameInstall {
    GameInstall {
        id: "g-caches".into(), kind: GameKind::Client, path: game.to_path_buf(), exe: "enshrouded.exe".into(),
        app_id: kiln_manage::CLIENT_APP_ID, library: None, platform: Platform::Proton, prefix: None, build_id: None,
        exe_sha256: None, steam_flavor: None, path_mode: PathMode::Copy, source: "manual".into(),
    }
}

#[test]
fn a_bundle_replaced_at_its_path_lists_as_the_new_one() {
    let t = tmp("bundle");
    let dirs = DataDirs::at(t.0.join("data"));
    let a = bundle::create(&stage(&t.0, "a"), &t.0.join("out-a"), &CreateOptions { source_date_epoch: 1_793_000_000, git: "aaaaaaa".into() }).unwrap();
    let b = bundle::create(&stage(&t.0, "b"), &t.0.join("out-b"), &CreateOptions { source_date_epoch: 1_793_000_000, git: "bbbbbbb".into() }).unwrap();
    let imported = bundle::import(&dirs, &a).unwrap();
    let first = bundle::list(&dirs).unwrap();
    assert_eq!(first.len(), 1);
    assert_eq!(first[0].version, imported.version);
    assert_eq!(bundle::list(&dirs).unwrap(), first, "an unchanged zip lists the same (from the caches)");
    // another build at the same file name: manifest and sha256 are the new zip's
    let bytes = std::fs::read(&b).unwrap();
    replace_keeping_mtime(&imported.file, &bytes);
    let second = bundle::list(&dirs).unwrap();
    assert_eq!(second[0].sha256, kiln_manage::hash::sha256_hex(&bytes));
    assert_eq!(bundle::read(&imported.file).unwrap().version, bundle::read(&b).unwrap().version);
    assert_ne!(second[0].version, first[0].version);
}

#[test]
fn an_installed_file_edited_back_to_its_size_and_mtime_is_still_modified() {
    let t = tmp("status");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("game");
    put(&game, "enshrouded.exe", b"MZ");
    let zip = bundle::create(&stage(&t.0, "s"), &t.0.join("out"), &CreateOptions { source_date_epoch: 1_793_000_000, git: "ccccccc".into() }).unwrap();
    let g = client(&game);
    install::install(&dirs, &g, &zip, &InstallOptions::default(), &mut NoProgress).unwrap();
    assert!(install::status(&dirs, &g).unwrap().modified.is_empty());
    let core = game.join("kiln/kiln-core.dll");
    let len = std::fs::metadata(&core).unwrap().len() as usize;
    replace_keeping_mtime(&core, &vec![b'x'; len]);
    assert_eq!(install::status(&dirs, &g).unwrap().modified, ["kiln/kiln-core.dll"]);
    // and the proxy: replaced by another DLL (EMF's), its class follows
    let emf = build_test_dll(&["DirectInput8Create", "emf_loader_identity"], b"");
    std::fs::write(game.join("dinput8.dll"), &emf).unwrap();
    assert_eq!(install::status(&dirs, &g).unwrap().proxy.unwrap().kind, pe::ProxyKind::Emf);
    assert_eq!(pe::classify_proxy(&game.join("dinput8.dll")).unwrap().kind, pe::ProxyKind::Emf);
    std::fs::remove_file(game.join("dinput8.dll")).unwrap();
    assert_eq!(pe::classify_proxy(&game.join("dinput8.dll")).unwrap().kind, pe::ProxyKind::None, "a removed DLL is gone");
}

#[test]
fn hooks_json_rewritten_reads_as_written() {
    let t = tmp("hooks");
    let game = t.0.join("game");
    assert!(compat::read_hooks(&game).unwrap().is_none());
    let write = |phase: &str| put(&game, "kiln/state/hooks.json", format!(r#"{{"format":1,"phase":"{phase}","targets":[]}}"#).as_bytes());
    write("boot.");
    assert_eq!(compat::read_hooks(&game).unwrap().unwrap().phase.as_deref(), Some("boot."));
    let p = game.join("kiln/state/hooks.json");
    replace_keeping_mtime(&p, br#"{"format":1,"phase":"world","targets":[]}"#);
    assert_eq!(compat::read_hooks(&game).unwrap().unwrap().phase.as_deref(), Some("world"));
    std::fs::write(&p, b"{ not json").unwrap();
    assert!(compat::read_hooks(&game).is_err(), "a broken file is an error, not the last good parse");
    std::fs::remove_file(&p).unwrap();
    assert!(compat::read_hooks(&game).unwrap().is_none());
}

#[cfg(target_os = "linux")]
#[test]
fn the_game_process_is_found_followed_and_lost() {
    let t = tmp("proc");
    let game = t.0.join("game");
    std::fs::create_dir_all(&game).unwrap();
    // a stand-in for the game: a copy of sleep named like it, its working folder the game's
    let exe = t.0.join("bin/enshrouded.exe");
    std::fs::create_dir_all(exe.parent().unwrap()).unwrap();
    std::fs::copy("/bin/sleep", &exe).unwrap();
    let g = client(&game);
    assert!(kiln_manage::proc::find_game(&g).unwrap().is_none());
    let mut child = std::process::Command::new(&exe).arg("30").current_dir(&game).spawn().unwrap();
    let t0 = SystemTime::now();
    let found = loop {
        if let Some(p) = kiln_manage::proc::find_game(&g).unwrap() {
            break p;
        }
        assert!(t0.elapsed().unwrap().as_secs() < 5, "the stand-in never showed");
        std::thread::sleep(std::time::Duration::from_millis(20));
    };
    assert_eq!(found.pid, child.id());
    // the full listing agrees with the lighter lookup
    let listed = kiln_manage::proc::list().unwrap().into_iter().find(|p| p.pid == found.pid).unwrap();
    assert_eq!(listed, found);
    assert_eq!(kiln_manage::proc::still_running(found.pid, &game, "enshrouded.exe"), Some(found.clone()));
    assert_eq!(kiln_manage::proc::still_running(found.pid, &t.0, "enshrouded.exe"), None, "another folder's game");
    assert_eq!(kiln_manage::proc::still_running(std::process::id(), &game, "enshrouded.exe"), None, "another process");
    child.kill().unwrap();
    child.wait().unwrap();
    assert_eq!(kiln_manage::proc::still_running(found.pid, &game, "enshrouded.exe"), None, "it ended");
    assert!(kiln_manage::proc::find_game(&g).unwrap().is_none());
}
