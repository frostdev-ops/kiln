//! launch::play's flow without Steam: a recording opener, a fake save folder.

use kiln_manage::game::{GameInstall, GameKind, PathMode, Platform};
use kiln_manage::launch::{self, LaunchMode, LaunchRequest};
use kiln_manage::paths::DataDirs;
use kiln_manage::playonce::{self, PlayOnceStatus};
use kiln_manage::saves::SaveDir;
use std::cell::RefCell;
use std::path::PathBuf;

#[test]
fn modes_write_play_once_backup_on_change_and_open_steam() {
    let root = std::env::temp_dir().join(format!("km-launch-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&root);
    let game = root.join("game");
    std::fs::create_dir_all(game.join("kiln/mods/hello")).unwrap();
    std::fs::write(game.join("enshrouded.exe"), b"MZ").unwrap();
    std::fs::write(game.join("kiln/mods/hello/kiln.mod.json"), br#"{"id":"hello","version":"1.0.0","apiVersion":"0.2","entryAssembly":"H.dll"}"#).unwrap();
    let saves_dir = root.join("remote");
    std::fs::create_dir_all(&saves_dir).unwrap();
    std::fs::write(saves_dir.join("3ad85aea"), b"world").unwrap();
    let saves = vec![SaveDir { label: "steam-1".into(), path: saves_dir.clone(), steam_id: Some("1".into()), recursive: false }];
    let dirs = DataDirs::at(root.join("data"));
    let inst = GameInstall {
        id: "g-launch".into(), kind: GameKind::Client, path: game.clone(), exe: "enshrouded.exe".into(),
        app_id: kiln_manage::CLIENT_APP_ID, library: None, platform: Platform::Proton, prefix: None, build_id: None,
        exe_sha256: None, steam_flavor: None, path_mode: PathMode::Copy, source: "manual".into(),
    };
    let opened: RefCell<Vec<String>> = RefCell::new(Vec::new());
    let opener = |u: &str| {
        opened.borrow_mut().push(u.to_string());
        Ok(())
    };
    let req = |mode| LaunchRequest { mode, profile_id: None, skip_backup: false };
    // the first launch backs up (no previous fingerprint), writes no flag in mode profile
    let r = launch::play_with(&dirs, &inst, &req(LaunchMode::Profile), Some(&saves), &opener).unwrap();
    assert_eq!(r.uri, "steam://rungameid/1203620");
    assert!(r.nonce.is_none() && r.backup.is_some());
    assert!(playonce::pending(&game).unwrap().is_none());
    // the same mod set again: no backup
    assert!(launch::play_with(&dirs, &inst, &req(LaunchMode::Profile), Some(&saves), &opener).unwrap().backup.is_none());
    // vanilla: another (empty) mod set -> a backup and a play-once flag the proxy accepts
    let v = launch::play_with(&dirs, &inst, &req(LaunchMode::Vanilla), Some(&saves), &opener).unwrap();
    assert!(v.backup.is_some());
    let bytes = std::fs::read(game.join("kiln/play-once.ini")).unwrap();
    let d = playonce::decide_detail(&bytes, kiln_manage::util::now_unix(), false, true);
    assert_eq!(d.status, PlayOnceStatus::Vanilla);
    assert_eq!(d.flag.unwrap().nonce, v.nonce.clone().unwrap());
    // no-mods has the same (empty) fingerprint as vanilla: no new backup
    let n = launch::play_with(&dirs, &inst, &req(LaunchMode::NoMods), Some(&saves), &opener).unwrap();
    assert!(n.backup.is_none());
    // skip_backup and mode once without a profile
    assert!(launch::play_with(&dirs, &inst, &LaunchRequest { mode: LaunchMode::Profile, profile_id: None, skip_backup: true },
        Some(&saves), &opener).unwrap().backup.is_none());
    assert!(launch::play_with(&dirs, &inst, &req(LaunchMode::Once), Some(&saves), &opener).is_err());
    assert_eq!(opened.borrow().len(), 5);
    assert!(kiln_manage::saves::backups(&dirs, &inst).unwrap().len() >= 2);
    // a failed opener leaves no flag behind
    let fail = |_: &str| Err(kiln_manage::Error::new(kiln_manage::ErrorCode::Unsupported, "no desktop"));
    assert!(launch::play_with(&dirs, &inst, &req(LaunchMode::Vanilla), Some(&saves), &fail).is_err());
    assert!(playonce::pending(&game).unwrap().is_none());
    let _ = std::fs::remove_dir_all(&root);
    let _: PathBuf = root;
}
