//! Direct starts (dev.29.17, `kiln_manage::direct`): the command per platform and runner (built, never run), what a
//! custom folder is, the settings in the install record, the launch flow through a recording starter, and whether
//! Kiln knows a game build (a fixture build record, a runtime bundle's index). Nothing here starts a program.

use kiln_manage::direct::{self, DirectCommand, RunnerInfo, RunnerKind, StartMode, StartSettings};
use kiln_manage::game::{self, GameInstall, GameKind, PathMode, Platform};
use kiln_manage::launch::{self, LaunchMode, LaunchRequest, Starter};
use kiln_manage::paths::DataDirs;
use kiln_manage::steam::{SteamFlavor, SteamRoot};
use std::cell::RefCell;
use std::path::{Path, PathBuf};

struct Tmp(PathBuf);
impl Drop for Tmp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
fn tmp(tag: &str) -> Tmp {
    let p = std::env::temp_dir().join(format!("km-direct-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&p);
    std::fs::create_dir_all(&p).unwrap();
    Tmp(p)
}

fn put(p: &Path, bytes: &[u8]) {
    std::fs::create_dir_all(p.parent().unwrap()).unwrap();
    std::fs::write(p, bytes).unwrap();
}

#[cfg(unix)]
fn exec(p: &Path) {
    use std::os::unix::fs::PermissionsExt;
    put(p, b"#!/bin/sh\nexit 0\n");
    std::fs::set_permissions(p, std::fs::Permissions::from_mode(0o755)).unwrap();
}

fn client(path: &Path, source: &str, library: Option<PathBuf>) -> GameInstall {
    GameInstall {
        id: "g-direct".into(), kind: GameKind::Client, path: path.to_path_buf(), exe: "enshrouded.exe".into(),
        app_id: kiln_manage::CLIENT_APP_ID, library, platform: Platform::Proton, prefix: None, build_id: None,
        exe_sha256: None, steam_flavor: None, path_mode: PathMode::Copy, source: source.into(),
    }
}

fn env<'a>(c: &'a DirectCommand, k: &str) -> Option<&'a str> {
    c.env.iter().find(|(n, _)| n == k).map(|(_, v)| v.as_str())
}

#[test]
fn windows_runs_the_exe_in_the_game_folder_with_nothing_set() {
    let g = client(Path::new("/games/Enshrouded-old"), "manual", None);
    let c = direct::command_for(true, &g, None, None, None, false).unwrap();
    assert_eq!(c.program, Path::new("/games/Enshrouded-old").join("enshrouded.exe"));
    assert!(c.args.is_empty());
    assert_eq!(c.cwd, Path::new("/games/Enshrouded-old"));
    assert!(c.env.is_empty(), "dinput8.dll next to the exe is loaded first on Windows: {:?}", c.env);
    assert!(c.runner.is_none() && c.prefix.is_none());
    // the same with Ultimate ASI Loader chained: Windows needs no override
    assert!(direct::command_for(true, &g, None, None, None, true).unwrap().env.is_empty());
}

#[test]
fn proton_runs_the_exe_with_compatdata_a_client_folder_and_the_proxy_override() {
    let g = client(Path::new("/games/Enshrouded-old"), "manual", None);
    let proton = RunnerInfo { kind: RunnerKind::Proton, path: "/tools/GE-Proton10-25".into(), label: "GE-Proton10-25".into(), source: "compat".into(), proton: None };
    let c = direct::command_for(false, &g, Some(&proton), Some(Path::new("/pfx/g-direct")), Some(Path::new("/steam")), false).unwrap();
    assert_eq!(c.program, Path::new("/tools/GE-Proton10-25/proton"));
    assert_eq!(c.args, vec!["run".to_string(), "/games/Enshrouded-old/enshrouded.exe".to_string()]);
    assert_eq!(c.cwd, Path::new("/games/Enshrouded-old"));
    assert_eq!(env(&c, "STEAM_COMPAT_DATA_PATH"), Some("/pfx/g-direct"));
    assert_eq!(env(&c, "STEAM_COMPAT_CLIENT_INSTALL_PATH"), Some("/steam"));
    assert_eq!(env(&c, "WINEDLLOVERRIDES"), Some("dinput8=n,b"), "Kiln's proxy native first");
    assert_eq!(c.prefix.as_deref(), Some(Path::new("/pfx/g-direct/pfx")));
    // Kiln never tells the Steam API an app id: Steam does that when it starts a game
    for k in ["SteamAppId", "SteamGameId", "STEAM_COMPAT_APP_ID", "GAMEID"] {
        assert!(env(&c, k).is_none(), "{k} must not be set");
        assert!(c.env_remove.iter().any(|r| r == k), "{k} inherited from the launcher is dropped");
    }
    for k in ["WINEPREFIX", "WINEDLLOVERRIDES", "LD_PRELOAD"] {
        assert!(c.env_remove.iter().any(|r| r == k), "{k}");
    }
    // a chained Ultimate ASI Loader needs xinput9_1_0 native first too
    let c = direct::command_for(false, &g, Some(&proton), Some(Path::new("/pfx/g-direct")), Some(Path::new("/steam")), true).unwrap();
    assert_eq!(env(&c, "WINEDLLOVERRIDES"), Some("dinput8,xinput9_1_0=n,b"));
    // Proton needs a folder to name in STEAM_COMPAT_CLIENT_INSTALL_PATH (plan gives Kiln's own without Steam); nothing
    // at all without a runner
    let e = direct::command_for(false, &g, Some(&proton), Some(Path::new("/pfx/g-direct")), None, false).unwrap_err();
    assert!(e.message.contains("STEAM_COMPAT_CLIENT_INSTALL_PATH"), "{}", e.message);
    assert!(direct::command_for(false, &g, None, Some(Path::new("/pfx")), Some(Path::new("/steam")), false).is_err());
}

#[test]
fn wine_runs_the_exe_with_its_prefix_and_keeps_mscoree() {
    let g = client(Path::new("/games/Enshrouded-old"), "manual", None);
    let wine = RunnerInfo { kind: RunnerKind::Wine, path: "/usr/bin/wine".into(), label: "Wine".into(), source: "system".into(), proton: None };
    let c = direct::command_for(false, &g, Some(&wine), Some(Path::new("/pfx/mine")), None, false).unwrap();
    assert_eq!(c.program, Path::new("/usr/bin/wine"));
    assert_eq!(c.args, vec!["/games/Enshrouded-old/enshrouded.exe".to_string()]);
    assert_eq!(c.cwd, Path::new("/games/Enshrouded-old"));
    assert_eq!(env(&c, "WINEPREFIX"), Some("/pfx/mine"));
    let o = env(&c, "WINEDLLOVERRIDES").unwrap();
    assert_eq!(o, "mshtml=;dinput8=n,b");
    assert!(kiln_manage::wine::dinput8_native_first(o) && kiln_manage::wine::check_mscoree(o).is_ok());
    assert!(env(&c, "STEAM_COMPAT_DATA_PATH").is_none());
    assert_eq!(c.prefix.as_deref(), Some(Path::new("/pfx/mine")));
}

#[test]
fn servers_never_start_here() {
    let mut g = client(Path::new("/srv/server"), "manual", None);
    g.kind = GameKind::Server;
    g.exe = "enshrouded_server.exe".into();
    assert!(direct::command_for(true, &g, None, None, None, false).is_err());
    assert_eq!(direct::effective_mode(&g, Some(&StartSettings { mode: Some(StartMode::Direct), ..Default::default() })), StartMode::Steam);
}

#[test]
fn prefix_paths_take_a_compatdata_folder_or_a_wine_prefix() {
    let t = tmp("pfx");
    let own = t.0.join("own");
    assert_eq!(direct::prefix_paths(RunnerKind::Proton, None, &own).unwrap(), (own.clone(), own.join("pfx")));
    // a Steam compatdata folder (pfx inside), and its pfx picked by mistake: the same pair
    let compat = t.0.join("compatdata/1203620");
    put(&compat.join("pfx/system.reg"), b"WINE REGISTRY Version 2\n");
    let want = (compat.clone(), compat.join("pfx"));
    assert_eq!(direct::prefix_paths(RunnerKind::Proton, Some(&compat), &own).unwrap(), want);
    assert_eq!(direct::prefix_paths(RunnerKind::Proton, Some(&compat.join("pfx")), &own).unwrap(), want);
    // a plain Wine prefix: Wine takes it as it is, Proton refuses it with what to do
    let wp = t.0.join("wineprefix");
    put(&wp.join("system.reg"), b"WINE REGISTRY Version 2\n");
    assert_eq!(direct::prefix_paths(RunnerKind::Wine, Some(&wp), &own).unwrap(), (wp.clone(), wp.clone()));
    assert!(direct::prefix_paths(RunnerKind::Proton, Some(&wp), &own).unwrap_err().message.contains("Proton"));
    // Wine given a compatdata folder uses its pfx; an empty folder becomes the prefix
    assert_eq!(direct::prefix_paths(RunnerKind::Wine, Some(&compat), &own).unwrap(), want);
    let empty = t.0.join("fresh");
    assert_eq!(direct::prefix_paths(RunnerKind::Wine, Some(&empty), &own).unwrap(), (empty.clone(), empty.clone()));
    assert!(direct::prefix_paths(RunnerKind::Wine, Some(Path::new("relative/pfx")), &own).is_err());
}

#[test]
fn own_prefixes_live_outside_the_data_folder() {
    use kiln_manage::paths::DataEnv;
    let e = DataEnv { home: Some("/h".into()), ..Default::default() };
    assert_eq!(direct::own_prefix(&e, "abc"), Some(PathBuf::from("/h/.local/share/kiln-prefixes/abc")));
    let e = DataEnv { home: Some("/h".into()), xdg_data_home: Some("/x".into()), ..Default::default() };
    assert_eq!(direct::own_prefix(&e, "abc"), Some(PathBuf::from("/x/kiln-prefixes/abc")));
    assert_ne!(direct::prefixes_root(&e), e.default_root().ok(), "never the data folder itself");
    assert_eq!(direct::own_prefix(&DataEnv { windows: true, ..Default::default() }, "abc"), None);
}

#[test]
fn umu_runs_the_exe_in_the_prefix_with_umus_default_game_id() {
    let g = client(Path::new("/games/Enshrouded-old"), "manual", None);
    let umu = RunnerInfo { kind: RunnerKind::Umu, path: "/usr/bin/umu-run".into(), label: "umu-run".into(), source: "system".into(),
        proton: Some("/tools/GE-Proton10-25".into()) };
    let c = direct::command_for(false, &g, Some(&umu), Some(Path::new("/pfx/g-direct/pfx")), None, false).unwrap();
    assert_eq!(c.program, Path::new("/usr/bin/umu-run"));
    assert_eq!(c.args, vec!["/games/Enshrouded-old/enshrouded.exe".to_string()]);
    assert_eq!(c.cwd, Path::new("/games/Enshrouded-old"));
    assert_eq!(env(&c, "WINEPREFIX"), Some("/pfx/g-direct/pfx"), "umu is given the prefix itself");
    assert_eq!(env(&c, "PROTONPATH"), Some("/tools/GE-Proton10-25"), "a Proton found here: umu downloads nothing");
    assert_eq!(env(&c, "WINEDLLOVERRIDES"), Some("dinput8=n,b"));
    // umu-<appid> would make umu set SteamAppId/SteamGameId: Kiln gives umu's own default
    assert_eq!(env(&c, "GAMEID"), Some("umu-default"));
    for k in ["SteamAppId", "SteamGameId", "STEAM_COMPAT_APP_ID", "STEAM_COMPAT_CLIENT_INSTALL_PATH", "STEAM_COMPAT_DATA_PATH"] {
        assert!(env(&c, k).is_none(), "{k} must not be set");
    }
    for k in ["GAMEID", "PROTONPATH", "SteamAppId", "WINEPREFIX"] {
        assert!(c.env_remove.iter().any(|r| r == k), "{k} inherited from the launcher is dropped");
    }
    assert_eq!(c.prefix.as_deref(), Some(Path::new("/pfx/g-direct/pfx")));
    // no Proton found: umu's own (no PROTONPATH)
    let own = RunnerInfo { proton: None, ..umu };
    assert!(env(&direct::command_for(false, &g, Some(&own), Some(Path::new("/pfx")), None, false).unwrap(), "PROTONPATH").is_none());
}

#[cfg(unix)]
#[test]
fn runners_are_found_without_steam_and_automatic_takes_umu_then_proton_then_wine() {
    let t = tmp("runners");
    let home = t.0.join("home");
    let bin = t.0.join("bin");
    let host = |path: &[&Path]| direct::Host {
        home: Some(home.clone()), xdg_data_home: None, path: Some(std::env::join_paths(path).unwrap()), shared: Vec::new(),
    };
    // nothing at all: no runner, and the problem says what to install (no Steam in it)
    let none = direct::runners(&[], &host(&[&bin]));
    assert!(none.is_empty(), "{none:?}");
    assert!(direct::default_runner(&none).is_none());
    assert!(!direct::NO_RUNNER.contains("Steam"));
    // wine on PATH only
    exec(&bin.join("wine"));
    let all = direct::runners(&[], &host(&[&bin]));
    assert_eq!(direct::default_runner(&all).unwrap().kind, RunnerKind::Wine);
    // GE-Proton in its usual places, none of which needs Steam: Steam's compatibilitytools.d folder without a Steam
    // (no steamapps: no Steam root), Heroic's and Lutris' Proton folders
    for (dir, ge) in [(".local/share/Steam/compatibilitytools.d", "GE-Proton9-27"), (".config/heroic/tools/proton", "GE-Proton10-4"),
                      (".local/share/lutris/runners/proton", "GE-Proton8-32")] {
        let d = home.join(dir).join(ge);
        exec(&d.join("proton"));
        exec(&d.join("files/bin/wine"));
    }
    assert!(kiln_manage::steam::roots_at(&home).is_empty(), "no Steam is installed in this world");
    let all = direct::runners(&[], &host(&[&bin]));
    let names: Vec<&str> = all.iter().map(|r| r.label.as_str()).collect();
    assert_eq!(names[..3], ["GE-Proton10-4", "GE-Proton9-27", "GE-Proton8-32"], "{names:?}");
    assert!(all[..3].iter().all(|r| r.kind == RunnerKind::Proton && r.source == "compat"));
    assert_eq!(all[3].kind, RunnerKind::Wine);
    assert_eq!(direct::default_runner(&all).unwrap().label, "GE-Proton10-4", "a Proton before Wine");
    // umu-run on PATH: first, running the newest GE-Proton found
    exec(&bin.join("umu-run"));
    let all = direct::runners(&[], &host(&[&bin]));
    let auto = direct::default_runner(&all).unwrap();
    assert_eq!((auto.kind, auto.label.as_str(), auto.source.as_str()), (RunnerKind::Umu, "umu-run", "system"));
    assert_eq!(auto.proton.as_deref(), Some(home.join(".config/heroic/tools/proton/GE-Proton10-4").canonicalize().unwrap().as_path()));
    // a relative PATH entry is never searched
    assert!(direct::runners(&[], &host(&[Path::new("bin")])).iter().all(|r| r.kind == RunnerKind::Proton));

    // with Steam installed too: its Valve Proton builds are offered after the compatibility tools; Steam's own
    // mapping of the game to a Proton is not read (automatic stays umu-run)
    let root = t.0.join("Steam");
    let valve = root.join("steamapps/common/Proton 9.0");
    exec(&valve.join("proton"));
    std::fs::create_dir_all(root.join("steamapps/common/Enshrouded")).unwrap(); // a game, not a runner
    put(&root.join("config/config.vdf"), br#""InstallConfigStore" { "Software" { "Valve" { "Steam" { "CompatToolMapping" { "1203620" { "name" "proton_9" "config" "" "priority" "250" } } } } } }"#);
    let roots = vec![SteamRoot { path: root.clone(), flavor: SteamFlavor::Native }];
    let all = direct::runners(&roots, &host(&[&bin]));
    let valve_at = all.iter().position(|r| r.label == "Proton 9.0").unwrap();
    assert_eq!(all[valve_at].source, "steam");
    assert!(all[..valve_at].iter().all(|r| r.kind != RunnerKind::Wine) && all[valve_at + 1..].iter().all(|r| r.kind == RunnerKind::Wine));
    assert_eq!(direct::default_runner(&all).unwrap().kind, RunnerKind::Umu);
    assert_ne!(direct::umu_proton(&all).as_deref(), Some(valve.as_path()), "umu never runs Valve's build");

    // the player's own path: a Proton folder, a umu-run, a wine binary, a wine folder; relative or empty refused
    assert_eq!(direct::runner_at(&valve).unwrap().kind, RunnerKind::Proton);
    assert_eq!(direct::runner_at(&valve.join("proton")).unwrap().path, valve);
    assert_eq!(direct::runner_at(&bin.join("umu-run")).unwrap().kind, RunnerKind::Umu);
    let wine = t.0.join("wine-9/bin/wine");
    exec(&wine);
    assert_eq!(direct::runner_at(&wine).unwrap().kind, RunnerKind::Wine);
    assert_eq!(direct::runner_at(&t.0.join("wine-9")).unwrap().path, wine);
    assert!(direct::runner_at(Path::new("wine")).is_err());
    assert!(direct::runner_at(&t.0.join("nothing")).is_err());
    // a picked umu-run gets the Proton found here
    let picked = direct::resolve_runner(&StartSettings { runner: Some(bin.join("umu-run")), ..Default::default() }, &all).unwrap();
    assert!(picked.proton.is_some());
}

#[test]
fn a_folder_no_steam_library_knows_is_custom_and_starts_directly() {
    let t = tmp("custom");
    let data = t.0.join("data");
    // Steam's own copy: a library with the app manifest
    let lib = t.0.join("lib");
    let steam_game = lib.join("steamapps/common/Enshrouded");
    put(&steam_game.join("enshrouded.exe"), b"MZ steam");
    put(&lib.join("steamapps/appmanifest_1203620.acf"), br#""AppState" { "appid" "1203620" "installdir" "Enshrouded" "buildid" "26430625" "StateFlags" "4" }"#);
    let roots = vec![SteamRoot { path: lib.clone(), flavor: SteamFlavor::Native }];
    let s = game::inspect_with(&steam_game, &roots, &data).unwrap();
    assert_eq!(s.source, "steam");
    assert!(!direct::is_custom(&s));
    assert_eq!(direct::effective_mode(&s, None), StartMode::Steam);
    // an older copy next to it in the same library folder, and one on another drive: neither is Steam's
    for dir in [lib.join("steamapps/common/Enshrouded-old"), t.0.join("elsewhere/Enshrouded 2025")] {
        put(&dir.join("enshrouded.exe"), b"MZ old");
        let g = game::inspect_with(&dir, &roots, &data).unwrap();
        assert_eq!((g.source.as_str(), g.library.as_deref()), ("manual", None), "{}", dir.display());
        assert!(direct::is_custom(&g));
        assert_eq!(direct::effective_mode(&g, None), StartMode::Direct, "the default for a custom folder");
        assert_eq!(direct::effective_mode(&g, Some(&StartSettings { mode: Some(StartMode::Steam), ..Default::default() })), StartMode::Steam, "the player's choice wins");
    }
    // a folder without the exe is not a game folder
    std::fs::create_dir_all(t.0.join("empty")).unwrap();
    assert!(game::inspect_with(&t.0.join("empty"), &roots, &data).is_err());
}

#[test]
fn the_start_choice_lives_in_the_record_and_survives_rewrites() {
    let t = tmp("record");
    let dirs = DataDirs::at(t.0.join("data"));
    let folder = t.0.join("old-build");
    put(&folder.join("enshrouded.exe"), b"MZ old");
    let roots: Vec<SteamRoot> = Vec::new();
    let g = game::add_with(&dirs, &folder, &roots).unwrap();
    assert!(direct::is_custom(&g));
    assert_eq!(direct::mode_of(&dirs, &g), StartMode::Direct);
    assert!(game::record(&dirs, &g.id).unwrap().unwrap().start.is_none(), "defaults are not written");
    // through Steam after all: kept, and kept when something writes the record again (an install of Kiln, Add again)
    let steam = StartSettings { mode: Some(StartMode::Steam), ..Default::default() };
    game::set_start(&dirs, &g.id, &steam, &roots).unwrap();
    assert_eq!(game::record(&dirs, &g.id).unwrap().unwrap().start, Some(steam.clone()));
    game::write_record(&dirs, &g, None).unwrap();
    assert_eq!(game::record(&dirs, &g.id).unwrap().unwrap().start, Some(steam.clone()));
    game::add_with(&dirs, &folder, &roots).unwrap();
    assert_eq!(direct::mode_of(&dirs, &g), StartMode::Steam);
    // a relative runner or prefix is refused and changes nothing
    let bad = StartSettings { mode: Some(StartMode::Direct), prefix: Some("rel/pfx".into()), runner: None };
    if !cfg!(windows) {
        assert!(game::set_start(&dirs, &g.id, &bad, &roots).is_err());
        assert_eq!(direct::mode_of(&dirs, &g), StartMode::Steam);
    }
    // back to the defaults: the record holds no choice again
    game::set_start(&dirs, &g.id, &StartSettings::default(), &roots).unwrap();
    assert!(game::record(&dirs, &g.id).unwrap().unwrap().start.is_none());
    // servers are not started here
    let srv = t.0.join("srv");
    put(&srv.join("enshrouded_server.exe"), b"MZ srv");
    let s = game::add_with(&dirs, &srv, &roots).unwrap();
    assert!(game::set_start(&dirs, &s.id, &StartSettings { mode: Some(StartMode::Direct), ..Default::default() }, &roots).is_err());
}

#[cfg(unix)]
#[test]
fn a_direct_start_under_linux_runs_in_the_chosen_prefix() {
    let t = tmp("linux-pfx");
    let dirs = DataDirs::at(t.0.join("data"));
    let folder = t.0.join("old-build");
    put(&folder.join("enshrouded.exe"), b"MZ old");
    let ge = t.0.join("tools/GE-Proton10-4");
    exec(&ge.join("proton"));
    let compat = t.0.join("prefixes/mine");
    let roots: Vec<SteamRoot> = Vec::new();
    let g = game::add_with(&dirs, &folder, &roots).unwrap();
    let pick = StartSettings { mode: Some(StartMode::Direct), runner: Some(ge.clone()), prefix: Some(compat.clone()) };
    let g2 = game::set_start(&dirs, &g.id, &pick, &roots).unwrap();
    assert_eq!(g2.platform, Platform::Proton);
    assert_eq!(g2.prefix.as_deref(), Some(compat.join("pfx").as_path()), "saves and path mode follow the prefix the game runs in");
    assert_eq!(game::known(&dirs).unwrap()[0].prefix, g2.prefix);
    // no Steam installed: Proton is told about an empty folder of Kiln's own, next to its prefixes
    let host = direct::Host { home: Some(t.0.join("home")), ..Default::default() };
    let c = direct::plan_on(&g2, &pick, &roots, &host).unwrap();
    assert_eq!(c.program, ge.join("proton"));
    assert_eq!(env(&c, "STEAM_COMPAT_DATA_PATH"), Some(compat.display().to_string().as_str()));
    let kiln_client = t.0.join("home/.local/share/kiln-prefixes/_compat-client");
    assert_eq!(env(&c, "STEAM_COMPAT_CLIENT_INSTALL_PATH"), Some(kiln_client.display().to_string().as_str()));
    // Steam installed: its folder
    let steam = vec![SteamRoot { path: t.0.join("Steam"), flavor: SteamFlavor::Native }];
    let c = direct::plan_on(&g2, &pick, &steam, &host).unwrap();
    assert_eq!(env(&c, "STEAM_COMPAT_CLIENT_INSTALL_PATH"), Some(t.0.join("Steam").display().to_string().as_str()));
    // with a plain Wine the same folder is the prefix itself
    let wine = t.0.join("wine/bin/wine");
    exec(&wine);
    let w = StartSettings { runner: Some(wine.clone()), ..pick.clone() };
    let g3 = game::set_start(&dirs, &g.id, &w, &roots).unwrap();
    assert_eq!((g3.platform, g3.prefix.as_deref()), (Platform::Wine, Some(compat.as_path())));
    let c = direct::plan_on(&g3, &w, &roots, &host).unwrap();
    assert_eq!((c.program.as_path(), env(&c, "WINEPREFIX")), (wine.as_path(), Some(compat.display().to_string().as_str())));
}

#[test]
fn the_launch_flow_hands_a_direct_start_to_the_starter_not_to_steam() {
    let t = tmp("flow");
    let dirs = DataDirs::at(t.0.join("data"));
    let game_dir = t.0.join("game");
    put(&game_dir.join("enshrouded.exe"), b"MZ");
    let inst = client(&game_dir, "manual", None);
    let cmd = direct::command_for(true, &inst, None, None, None, false).unwrap();
    let ran: RefCell<Vec<PathBuf>> = RefCell::new(Vec::new());
    let go = |c: &DirectCommand| {
        ran.borrow_mut().push(c.program.clone());
        Ok(())
    };
    let req = LaunchRequest { mode: LaunchMode::NoMods, profile_id: None, skip_backup: true };
    let r = launch::play_via(&dirs, &inst, &req, Some(&[]), Starter::Direct(&cmd, &go)).unwrap();
    assert_eq!(r.via, StartMode::Direct);
    assert!(r.uri.is_empty());
    assert_eq!(ran.borrow().as_slice(), [game_dir.join("enshrouded.exe")]);
    assert!(kiln_manage::playonce::pending(&game_dir).unwrap().is_some(), "the play-once flag is written as for Steam");
    // a start that fails takes its flag back
    let fail = |_: &DirectCommand| Err(kiln_manage::Error::invalid("no runner"));
    assert!(launch::play_via(&dirs, &inst, &req, Some(&[]), Starter::Direct(&cmd, &fail)).is_err());
    assert!(kiln_manage::playonce::pending(&game_dir).unwrap().is_none());
    // through Steam the URI is opened as before
    let r = launch::play_with(&dirs, &inst, &req, Some(&[]), &|_| Ok(())).unwrap();
    assert_eq!((r.via, r.uri.as_str()), (StartMode::Steam, "steam://rungameid/1203620"));
    // an older LaunchResult without `via` reads as a Steam start
    let old: launch::LaunchResult = serde_json::from_str(r#"{"gameId":"g","mode":"profile","nonce":null,"backup":null,"uri":"steam://rungameid/1203620","started":"2026-11-17T00:00:00Z"}"#).unwrap();
    assert_eq!(old.via, StartMode::Steam);
}

/// A build record as sigdb/builds/client/<sha256>.json has it (format 2's top fields; only `verified` is read).
fn record(sha: &str, verified: bool) -> String {
    format!(r#"{{"sha256":"{sha}","side":"client","record":2,"pdb":"enshrouded.pdb","steam_build":26430625,"verified":{verified},"note":"fixture"}}"#)
}

#[test]
fn the_build_lookup_reads_the_games_records_then_the_bundles_index() {
    let t = tmp("build");
    let game_dir = t.0.join("game");
    put(&game_dir.join("enshrouded.exe"), b"MZ a build kept for mods");
    let sha = kiln_manage::hash::sha256_file(&game_dir.join("enshrouded.exe")).unwrap().0;
    let inst = client(&game_dir, "manual", None);
    // neither Kiln in the game nor a runtime: nothing to say
    assert_eq!(direct::build_check(&inst, || None), None);
    // Kiln installed with records for other builds only: unknown
    let builds = game_dir.join("kiln/sigdb/builds/client");
    put(&builds.join(format!("{}.json", "c".repeat(64))), record(&"c".repeat(64), true).as_bytes());
    let b = direct::build_check(&inst, || None).unwrap();
    assert!(!b.known);
    assert_eq!((b.short.as_str(), b.from.as_str()), (&sha[..12], "game"));
    assert_eq!(direct::unknown_build_sentence(&b.short),
        format!("Kiln doesn't know this game build ({}). Kiln starts with the game but stays idle: no mods load.", &sha[..12]));
    // a record that is not verified: still idle under verified-only
    put(&builds.join(format!("{sha}.json")), record(&sha, false).as_bytes());
    assert!(!direct::build_check(&inst, || None).unwrap().known);
    put(&builds.join(format!("{sha}.json")), record(&sha, true).as_bytes());
    assert!(direct::build_check(&inst, || None).unwrap().known);
    // the runtime zip's index while Kiln is not in the game yet
    std::fs::remove_dir_all(game_dir.join("kiln")).unwrap();
    let m: kiln_manage::bundle::RuntimeManifest = serde_json::from_str(&std::fs::read_to_string(
        Path::new(env!("CARGO_MANIFEST_DIR")).join("../../schema/examples/kiln-runtime.json")).unwrap()).unwrap();
    let known = "ce6fdba63e27744943b695921383a29133285d359e07671f6ca33a47e2147618";
    assert!(direct::known_in_manifest(&m, kiln_manage::manifest::RunSide::Client, known));
    assert!(!direct::known_in_manifest(&m, kiln_manage::manifest::RunSide::Server, known));
    assert!(!direct::known_in_manifest(&m, kiln_manage::manifest::RunSide::Client, &sha));
    #[cfg(feature = "zip")]
    {
        let stage = t.0.join("stage");
        put(&stage.join("kiln-runtime.json"), format!(r#"{{"format":1,"kiln":"0.2.0-dev","abi":2,"api":"0.2.0",
            "components":{{"core":{{"sides":["client","server"],"default":true,"requires":[]}}}},
            "builds":{{"client":[{{"sha256":"{sha}","verified":true}}]}}}}"#).as_bytes());
        put(&stage.join("core/kiln/kiln-core.dll"), b"core");
        put(&stage.join("core/dinput8.dll"), &kiln_manage::pe::build_test_dll(&["DirectInput8Create", "GetdfDIJoystick", "KilnProxyInfo"], b"KilnProxy/2 test\0"));
        let zip = kiln_manage::bundle::create(&stage, &t.0.join("out"), &kiln_manage::bundle::CreateOptions { source_date_epoch: 1_793_620_800, git: "abcdef0".into() }).unwrap();
        let b = direct::build_check(&inst, || Some(zip.clone())).unwrap();
        assert!(b.known && b.from == "bundle");
        put(&game_dir.join("enshrouded.exe"), b"MZ another build");
        assert!(!direct::build_check(&inst, || Some(zip.clone())).unwrap().known, "the exe changed: hashed again");
    }
}

/// A PC with no Steam at all: no Steam folder, no library, no Steam client path, and on PATH only what the test puts
/// there. Discovery, Add a folder (also a Steam library left on a drive), Install Kiln, a profile, Play's command on
/// Windows and on Linux (Wine, Proton with Kiln's own client folder, umu-run), the launch flow through a recording
/// starter (nothing runs), the game list and status, and the support report. Installing Kiln needs the `zip` feature.
#[cfg(feature = "zip")]
#[test]
fn a_pc_without_steam_adds_installs_and_starts_a_game_folder() {
    let t = tmp("nosteam");
    let home = t.0.join("home");
    let bin = t.0.join("bin");
    std::fs::create_dir_all(&home).unwrap();
    std::fs::create_dir_all(&bin).unwrap();
    assert!(kiln_manage::steam::roots_at(&home).is_empty(), "no Steam in this world");
    let roots: Vec<SteamRoot> = Vec::new();
    let host = direct::Host { home: Some(home.clone()), xdg_data_home: None, path: Some(bin.clone().into_os_string()), shared: Vec::new() };
    let dirs = DataDirs::at(t.0.join("data"));

    // discovery: nothing found, nothing wrong
    assert!(game::detect_in(&roots, &dirs.root).unwrap().is_empty());
    // a copy of a Steam library left on a drive (its app manifest too): without Steam, Steam's Play cannot start it,
    // so it is a custom folder that starts directly, with the manifest's build id
    let lib = t.0.join("drive/SteamLibrary");
    let folder = lib.join("steamapps/common/Enshrouded");
    put(&folder.join("enshrouded.exe"), b"MZ a copy of the game");
    put(&lib.join("steamapps/appmanifest_1203620.acf"), br#""AppState" { "appid" "1203620" "installdir" "Enshrouded" "buildid" "26430625" "StateFlags" "4" }"#);
    let g = game::add_on(&dirs, &folder, &roots, &host).unwrap();
    assert!(direct::is_custom(&g), "{g:?}");
    assert_eq!((g.source.as_str(), g.library.as_deref(), g.build_id.as_deref()), ("manual", None, Some("26430625")));
    assert_eq!(direct::mode_of(&dirs, &g), StartMode::Direct);
    let own = home.join(".local/share/kiln-prefixes").join(&g.id);
    if cfg!(unix) {
        // no runner yet: the prefix a Proton or umu start would use (Kiln's own), never a Steam compatdata folder
        assert_eq!((g.platform, g.prefix.as_deref()), (Platform::Proton, Some(own.join("pfx").as_path())));
    }

    // Install Kiln into it (refuses only while Steam updates a manifest that says so: this one is fully installed)
    #[cfg(feature = "zip")]
    let g = {
        let stage = t.0.join("stage");
        put(&stage.join("kiln-runtime.json"), br#"{"format":1,"kiln":"0.2.0-dev","abi":2,"api":"0.2.0",
            "components":{"core":{"sides":["client","server"],"default":true,"requires":[]}}}"#);
        put(&stage.join("core/kiln/kiln-core.dll"), b"core");
        put(&stage.join("core/dinput8.dll"), &kiln_manage::pe::build_test_dll(&["DirectInput8Create", "GetdfDIJoystick", "KilnProxyInfo"], b"KilnProxy/2 test\0"));
        let zip = kiln_manage::bundle::create(&stage, &t.0.join("out"), &kiln_manage::bundle::CreateOptions { source_date_epoch: 1_793_620_800, git: "abcdef0".into() }).unwrap();
        let b = kiln_manage::bundle::import(&dirs, &zip).unwrap();
        let opts = kiln_manage::install::InstallOptions { installer: Some("kiln-launcher test".into()), ..Default::default() };
        kiln_manage::install::install(&dirs, &g, &b.file, &opts, &mut kiln_manage::task::NoProgress).unwrap();
        assert!(folder.join("dinput8.dll").is_file());
        let again = game::add_on(&dirs, &folder, &roots, &host).unwrap();
        assert_eq!(again.id, g.id, "the receipt keeps the install's id");
        again
    };

    // a profile, activated in the game
    let p = kiln_manage::profile::create(&dirs, "Mine", None).unwrap();
    kiln_manage::profile::activate(&dirs, &p.id, &g).unwrap();
    assert!(folder.join("kiln/profile.json").is_file());

    // the game list and its status: starts directly, no Steam launch options, nothing of Steam updating
    let st = game::status_with(&dirs, &g, &roots).unwrap();
    assert_eq!(st.start, Some(StartMode::Direct));
    assert!(st.launch_options.is_none() && !st.steam_updating);
    #[cfg(feature = "zip")]
    assert!(st.runtime.installed);

    // Play's command on Windows: the exe in its folder, nothing set
    let win = direct::command_for(true, &g, None, None, None, false).unwrap();
    assert_eq!((win.program.as_path(), win.cwd.as_path()), (folder.join("enshrouded.exe").as_path(), folder.as_path()));
    assert!(win.env.is_empty());

    #[cfg(unix)]
    {
        let settings = StartSettings::default();
        let setup = direct::setup_on(&dirs, &g, &roots, &host);
        assert!(setup.command.is_none() && setup.problem.as_deref() == Some(direct::NO_RUNNER), "{setup:?}");
        assert!(!setup.steam_found);
        // plain Wine on PATH, in Kiln's own prefix
        exec(&bin.join("wine"));
        let c = direct::plan_on(&g, &settings, &roots, &host).unwrap();
        assert_eq!(c.program, bin.join("wine").canonicalize().unwrap());
        assert_eq!(env(&c, "WINEPREFIX"), Some(own.join("pfx").display().to_string().as_str()));
        assert_eq!(env(&c, "WINEDLLOVERRIDES"), Some("mshtml=;dinput8=n,b"));
        // GE-Proton in ~/.local/share/Steam/compatibilitytools.d (no Steam there): Proton, told about Kiln's own folder
        let ge = home.join(".local/share/Steam/compatibilitytools.d/GE-Proton10-25");
        exec(&ge.join("proton"));
        exec(&ge.join("files/bin/wine"));
        assert!(kiln_manage::steam::roots_at(&home).is_empty(), "a compatibilitytools.d folder is not a Steam install");
        let c = direct::plan_on(&g, &settings, &roots, &host).unwrap();
        assert_eq!(c.program, ge.canonicalize().unwrap().join("proton"));
        assert_eq!(env(&c, "STEAM_COMPAT_DATA_PATH"), Some(own.display().to_string().as_str()));
        let client = home.join(".local/share/kiln-prefixes/_compat-client");
        assert_eq!(env(&c, "STEAM_COMPAT_CLIENT_INSTALL_PATH"), Some(client.display().to_string().as_str()));
        assert!(!client.exists(), "the command is only built: the folder is made at the start");
        // umu-run on PATH: automatic takes it, with that GE-Proton and umu's default game id
        exec(&bin.join("umu-run"));
        let setup = direct::setup_on(&dirs, &g, &roots, &host);
        assert_eq!(setup.default_runner.as_ref().map(|r| r.kind), Some(RunnerKind::Umu));
        let c = setup.command.clone().unwrap();
        assert_eq!(c.program, bin.join("umu-run").canonicalize().unwrap());
        assert_eq!(env(&c, "WINEPREFIX"), Some(own.join("pfx").display().to_string().as_str()));
        assert_eq!(env(&c, "PROTONPATH"), Some(ge.canonicalize().unwrap().display().to_string().as_str()));
        assert_eq!(env(&c, "GAMEID"), Some("umu-default"));
        for cmd in [&c, &direct::plan_on(&g, &StartSettings { runner: Some(ge.clone()), ..Default::default() }, &roots, &host).unwrap()] {
            for k in ["SteamAppId", "SteamGameId", "STEAM_COMPAT_APP_ID"] {
                assert!(env(cmd, k).is_none(), "{k}");
            }
            assert!(cmd.args.iter().chain(cmd.env.iter().map(|(_, v)| v)).all(|a| !a.contains("steam://")));
        }
        // the install follows the start: platform and prefix of a umu start
        let mut g2 = g.clone();
        game::apply_start_on(&mut g2, &settings, &roots, &dirs.root, &host);
        assert_eq!((g2.platform, g2.prefix.as_deref()), (Platform::Proton, Some(own.join("pfx").as_path())));

        // the launch flow with that command: the starter gets it, no URI, saves in the prefix backed up
        let saves_dir = own.join("pfx/drive_c/users/steamuser/Saved Games/Enshrouded");
        put(&saves_dir.join("3ad85aea_1"), b"a world");
        let saves = kiln_manage::saves::save_dirs_in(&g2, &roots);
        assert!(saves.iter().all(|s| s.steam_id.is_none()), "no Steam user folders without Steam: {saves:?}");
        assert!(saves.iter().any(|s| s.path == saves_dir), "{saves:?}");
        let ran: RefCell<Vec<DirectCommand>> = RefCell::new(Vec::new());
        let go = |c: &DirectCommand| {
            ran.borrow_mut().push(c.clone());
            Ok(())
        };
        let req = LaunchRequest { mode: LaunchMode::Profile, profile_id: None, skip_backup: false };
        let r = launch::play_via(&dirs, &g2, &req, Some(&saves), Starter::Direct(&c, &go)).unwrap();
        assert_eq!((r.via, r.uri.as_str()), (StartMode::Direct, ""));
        assert_eq!(ran.borrow().len(), 1);
        assert_eq!(ran.borrow()[0], c);
        assert!(r.backup.is_some(), "the saves in Kiln's prefix are backed up before the start");
    }

    // the support report: how it starts, no Steam launch options, no Steam compat tool, no "Steam running"
    let input = kiln_manage::report::ReportInput {
        install: Some(g.clone()), dirs: Some(dirs.clone()), tool: "kiln-launcher test".into(), roots: Some(Vec::new()),
        deck: Some(false), redactor: Some(kiln_manage::report::Redactor::new(None, None, None)), ..Default::default()
    };
    let r = kiln_manage::report::build(&input);
    assert!(r.summary.contains("Custom folder: yes"), "{}", r.summary);
    assert!(r.summary.contains("Start:         directly"), "{}", r.summary);
    for not in ["Steam running", "Launch opts:", "Compat tool:", "steam://"] {
        assert!(!r.summary.contains(not), "{not}: {}", r.summary);
    }
    assert!(r.findings.iter().all(|f| f.id != "proton-override" && !f.text.contains("Steam's launch options")), "{:?}", r.findings);
}
