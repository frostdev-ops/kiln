//! Steam discovery, game installs, path translation, saves, logs and launch helpers, on fake trees in temp dirs.
//! Nothing here reads or writes the real Steam folders (every call takes explicit roots).

use kiln_manage::game::{self, GameInstall, GameKind, PathMode, Platform};
use kiln_manage::paths::DataDirs;
use kiln_manage::steam::{self, SteamFlavor, SteamRoot};
use kiln_manage::{ErrorCode, launch, logs, saves, vdf, winpath};
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU32, Ordering};

fn repo() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../..")
}

fn fixture(name: &str) -> String {
    fs::read_to_string(repo().join("schema/fixtures/vdf").join(name)).unwrap()
}

static N: AtomicU32 = AtomicU32::new(0);

/// A fresh temp dir (canonical, so comparisons with canonicalized discovery results hold).
fn tmp(name: &str) -> PathBuf {
    let d = std::env::temp_dir().join(format!("kiln-disc-{}-{}-{name}", std::process::id(), N.fetch_add(1, Ordering::SeqCst)));
    let _ = fs::remove_dir_all(&d);
    fs::create_dir_all(&d).unwrap();
    steam::canon(&d)
}

fn write(p: &Path, text: impl AsRef<[u8]>) {
    fs::create_dir_all(p.parent().unwrap()).unwrap();
    fs::write(p, text).unwrap();
}

/// VDF text with a path inside: backslashes escaped like Steam writes them.
fn vdf_path(p: &Path) -> String {
    p.display().to_string().replace('\\', "\\\\")
}

struct FakeSteam {
    home: PathBuf,
    root: PathBuf,
    lib2: PathBuf,
    client: PathBuf,
    #[allow(dead_code)]
    server: PathBuf,
}

fn fake_steam(name: &str) -> FakeSteam {
    let home = tmp(name);
    let root = home.join(".local/share/Steam");
    let lib2 = home.join("games/SteamLibrary");
    fs::create_dir_all(root.join("steamapps/common")).unwrap();
    fs::create_dir_all(lib2.join("steamapps/common")).unwrap();
    write(
        &root.join("steamapps/libraryfolders.vdf"),
        fixture("libraryfolders.vdf").replace("@ROOT@", &vdf_path(&root)).replace("@LIB2@", &vdf_path(&lib2)),
    );
    write(&lib2.join("steamapps/appmanifest_1203620.acf"), fixture("appmanifest_1203620.acf"));
    write(&root.join("steamapps/appmanifest_2278520.acf"), fixture("appmanifest_2278520-updating.acf"));
    let client = lib2.join("steamapps/common/Enshrouded");
    let server = root.join("steamapps/common/EnshroudedServer");
    write(&client.join("enshrouded.exe"), b"MZ client");
    write(&server.join("enshrouded_server.exe"), b"MZ server");
    write(&root.join("config/config.vdf"), fixture("config.vdf"));
    write(&root.join("userdata/111/config/localconfig.vdf"), fixture("localconfig-override.vdf"));
    write(&root.join("userdata/222/config/localconfig.vdf"), fixture("localconfig-plain.vdf"));
    write(&root.join("userdata/333/config/localconfig.vdf"), "\"broken");
    FakeSteam { home, root, lib2, client, server }
}

fn root_of(f: &FakeSteam) -> SteamRoot {
    SteamRoot { path: f.root.clone(), flavor: steam::flavor_of(&f.root) }
}

#[test]
fn every_vdf_fixture_parses() {
    let dir = repo().join("schema/fixtures/vdf");
    let mut n = 0;
    for e in fs::read_dir(&dir).unwrap() {
        let p = e.unwrap().path();
        let v = vdf::read(&p).unwrap_or_else(|e| panic!("{}: {e}", p.display()));
        assert!(!v.entries().is_empty(), "{}", p.display());
        n += 1;
    }
    assert!(n >= 7);
    let old = steam::parse_libraryfolders(&fixture("libraryfolders-old.vdf")).unwrap();
    assert_eq!(old, vec![(PathBuf::from("@LIB2@"), vec![])]);
}

#[test]
fn libraries_manifests_compat_tool_and_launch_options() {
    let f = fake_steam("libs");
    let r = root_of(&f);
    let libs = steam::libraries(&r).unwrap();
    let paths: Vec<&Path> = libs.iter().map(|l| l.path.as_path()).collect();
    assert_eq!(paths, vec![f.root.as_path(), f.lib2.as_path()], "the unmounted library is dropped");
    assert_eq!(libs[0].apps, vec![2278520]);
    assert_eq!(libs[1].apps, vec![1203620], "apps come from the appmanifests, not the stale vdf list");

    let c = steam::app_manifest(&f.lib2, 1203620).unwrap().unwrap();
    assert_eq!((c.app_id, c.name.as_str(), c.build_id.as_deref(), c.state_flags, c.updating), (1203620, "Enshrouded", Some("26404966"), 4, false));
    assert_eq!(c.install_dir, f.client);
    let s = steam::app_manifest(&f.root, 2278520).unwrap().unwrap();
    assert!(s.updating, "StateFlags 1030 = installed + update required + update started");
    assert!(steam::app_manifest(&f.root, 1203620).unwrap().is_none());
    write(&f.root.join("steamapps/appmanifest_9.acf"), "\"AppState\" {");
    assert_eq!(steam::app_manifest(&f.root, 9).unwrap_err().code, ErrorCode::Parse);

    assert_eq!(steam::compat_tool(&r, 1203620).unwrap().as_deref(), Some("GE-Proton11-11-x86_64"));
    assert_eq!(steam::compat_tool(&r, 2278520).unwrap(), None, "an empty name is the default");
    assert_eq!(steam::compat_tool(&r, 42).unwrap(), None);

    let lo = steam::launch_options(&r, 1203620).unwrap();
    assert_eq!(lo.len(), 2, "the broken user file is skipped, the others stay: {lo:?}");
    assert_eq!(lo[0].steam_id, "111");
    assert_eq!(lo[0].value, "WINEDLLOVERRIDES=\"dinput8=n,b\" %command% -nointro");
    assert_eq!(lo[1].value, "gamemoderun %command%");
    assert!(steam::launch_options(&r, 2278520).unwrap().is_empty(), "empty launch options do not count");
    let _ = fs::remove_dir_all(&f.home);
}

#[cfg(unix)]
#[test]
fn roots_native_flatpak_snap_deduped() {
    let f = fake_steam("roots");
    fs::create_dir_all(f.home.join(".steam")).unwrap();
    std::os::unix::fs::symlink(&f.root, f.home.join(".steam/steam")).unwrap();
    std::os::unix::fs::symlink(&f.root, f.home.join(".steam/root")).unwrap();
    let flat = f.home.join(".var/app/com.valvesoftware.Steam/.local/share/Steam");
    fs::create_dir_all(flat.join("steamapps")).unwrap();
    let snap = f.home.join("snap/steam/common/.local/share/Steam");
    fs::create_dir_all(snap.join("steamapps")).unwrap();
    fs::create_dir_all(f.home.join(".var/app/com.valvesoftware.Steam/data/Steam")).unwrap(); // no steamapps: not a root
    let roots = steam::roots_at(&f.home);
    let got: Vec<(PathBuf, SteamFlavor)> = roots.iter().map(|r| (r.path.clone(), r.flavor)).collect();
    assert_eq!(got, vec![(f.root.clone(), SteamFlavor::Native), (flat.clone(), SteamFlavor::Flatpak), (snap.clone(), SteamFlavor::Snap)]);
    let list = std::env::join_paths([&flat, &f.root, &f.root]).unwrap();
    let from_env = steam::roots_from_list(&list);
    assert_eq!(from_env.iter().map(|r| r.flavor).collect::<Vec<_>>(), vec![SteamFlavor::Flatpak, SteamFlavor::Native]);
    let _ = fs::remove_dir_all(&f.home);
}

#[cfg(unix)]
#[test]
fn detect_inspect_and_path_modes() {
    let f = fake_steam("detect");
    let data = f.home.join("data/kiln");
    let pfx = f.lib2.join("steamapps/compatdata/1203620/pfx");
    fs::create_dir_all(pfx.join("dosdevices")).unwrap();
    std::os::unix::fs::symlink("/", pfx.join("dosdevices/z:")).unwrap();
    let roots = vec![root_of(&f)];
    let found = game::detect_in(&roots, &data).unwrap();
    assert_eq!(found.len(), 2, "{found:#?}");
    let c = &found[0];
    assert_eq!((c.kind, c.exe.as_str(), c.app_id, c.platform, c.path_mode), (GameKind::Client, "enshrouded.exe", 1203620, Platform::Proton, PathMode::Z));
    assert_eq!(c.path, f.client);
    assert_eq!(c.library.as_deref(), Some(f.lib2.as_path()));
    assert_eq!(c.prefix.as_deref(), Some(pfx.as_path()));
    assert_eq!((c.build_id.as_deref(), c.source.as_str(), c.steam_flavor), (Some("26404966"), "steam", Some(SteamFlavor::Native)));
    assert!(game::is_uuid(&c.id), "{}", c.id);
    assert_eq!(c.id, game::path_install_id(&c.path));
    let s = &found[1];
    assert_eq!((s.kind, s.app_id, s.path_mode), (GameKind::Server, 2278520, PathMode::Copy), "no server prefix yet: copy mode");

    // inspect of the same folder gives the same facts (exe path accepted too)
    let i = game::inspect_with(&f.client.join("enshrouded.exe"), &roots, &data).unwrap();
    assert_eq!(&i, c);

    // the id follows the receipt once Kiln is installed
    write(&f.client.join("kiln/.install.json"), r#"{"format":1,"installId":"c7a3e1f0-2b4d-4e6f-8a9b-0c1d2e3f4a5b"}"#);
    assert_eq!(game::install_id_for(&f.client), "c7a3e1f0-2b4d-4e6f-8a9b-0c1d2e3f4a5b");

    // a Z: link that does not point to / means copy mode; so do Flatpak roots and untranslatable data dirs
    fs::remove_file(pfx.join("dosdevices/z:")).unwrap();
    std::os::unix::fs::symlink(&f.home, pfx.join("dosdevices/z:")).unwrap();
    assert_eq!(game::path_mode_for(Platform::Proton, Some(SteamFlavor::Native), Some(&pfx), &data), PathMode::Copy);
    fs::remove_file(pfx.join("dosdevices/z:")).unwrap();
    std::os::unix::fs::symlink("/", pfx.join("dosdevices/z:")).unwrap();
    assert_eq!(game::path_mode_for(Platform::Proton, Some(SteamFlavor::Flatpak), Some(&pfx), &data), PathMode::Copy);
    assert_eq!(game::path_mode_for(Platform::Proton, None, Some(&pfx), Path::new("/odd/da:ta")), PathMode::Copy);
    assert_eq!(game::path_mode_for(Platform::Windows, None, None, &data), PathMode::Native);
    assert_eq!(game::path_mode(c, &DataDirs::at(&data)).unwrap(), PathMode::Z);

    // a manual folder with kilnctl's <dir>.pfx is plain Wine
    let manual = f.home.join("srv/lab");
    write(&manual.join("enshrouded_server.exe"), b"MZ");
    fs::create_dir_all(f.home.join("srv/lab.pfx/dosdevices")).unwrap();
    std::os::unix::fs::symlink("/", f.home.join("srv/lab.pfx/dosdevices/z:")).unwrap();
    let m = game::inspect_with(&manual, &roots, &data).unwrap();
    assert_eq!((m.kind, m.platform, m.source.as_str(), m.path_mode, m.library.clone()), (GameKind::Server, Platform::Wine, "manual", PathMode::Z, None));
    // a client copy outside Steam borrows the client's Steam prefix
    let copy = f.home.join("srv/client-copy-1");
    write(&copy.join("enshrouded.exe"), b"MZ");
    let cc = game::inspect_with(&copy, &roots, &data).unwrap();
    assert_eq!((cc.platform, cc.prefix.as_deref(), cc.path_mode), (Platform::Proton, Some(pfx.as_path()), PathMode::Z));
    assert_eq!(game::inspect_with(&f.home, &roots, &data).unwrap_err().code, ErrorCode::InvalidArgument);
    assert_eq!(game::inspect_with(&f.home.join("nope"), &roots, &data).unwrap_err().code, ErrorCode::NotFound);

    // flatpak root: copy mode
    let flat_home = tmp("flat");
    let flat = flat_home.join(".var/app/com.valvesoftware.Steam/.local/share/Steam");
    write(&flat.join("steamapps/appmanifest_1203620.acf"), fixture("appmanifest_1203620.acf"));
    write(&flat.join("steamapps/common/Enshrouded/enshrouded.exe"), b"MZ");
    let fr = steam::roots_at(&flat_home);
    let fg = game::detect_in(&fr, &data).unwrap();
    assert_eq!((fg.len(), fg[0].steam_flavor, fg[0].path_mode), (1, Some(SteamFlavor::Flatpak), PathMode::Copy));
    let _ = fs::remove_dir_all(&flat_home);
    let _ = fs::remove_dir_all(&f.home);
}

#[cfg(unix)]
#[test]
fn records_status_and_launch_options_info() {
    let f = fake_steam("status");
    let dirs = DataDirs::at(f.home.join("data/kiln"));
    let roots = vec![root_of(&f)];
    let found = game::detect_in(&roots, &dirs.root).unwrap();
    let (c, s) = (&found[0], &found[1]);
    assert!(game::known(&dirs).unwrap().is_empty());
    game::write_record(&dirs, c, None).unwrap();
    game::write_record(&dirs, s, Some(serde_json::json!({"installId": "x"}))).unwrap();
    let rec = game::record(&dirs, &s.id).unwrap().unwrap();
    assert_eq!(rec.format, 1);
    assert!(rec.added.is_some() && rec.receipt.is_some());
    assert_eq!(game::known(&dirs).unwrap().len(), 2);
    // a new id for the same folder replaces the old record
    let mut moved = c.clone();
    moved.id = "c7a3e1f0-2b4d-4e6f-8a9b-0c1d2e3f4a5b".into();
    game::write_record(&dirs, &moved, None).unwrap();
    let ids: Vec<String> = game::known(&dirs).unwrap().into_iter().map(|g| g.id).collect();
    assert!(ids.contains(&moved.id) && !ids.contains(&c.id), "{ids:?}");
    game::remove(&dirs, &s.id).unwrap();
    assert_eq!(game::remove(&dirs, &s.id).unwrap_err().code, ErrorCode::NotFound);
    assert_eq!(game::record(&dirs, "../x").unwrap_err().code, ErrorCode::InvalidArgument);

    // add: a manual server folder (exe hashed)
    let manual = f.home.join("srv/added");
    write(&manual.join("enshrouded_server.exe"), b"MZ added");
    fs::create_dir_all(f.home.join("srv/added.pfx/dosdevices")).unwrap();
    let a = game::add(&dirs, &manual).unwrap();
    assert_eq!(a.exe_sha256.as_deref(), Some(kiln_manage::hash::sha256_hex(b"MZ added").as_str()));
    assert!(game::known(&dirs).unwrap().iter().any(|g| g.id == a.id));

    // status: not running; the server's Steam update shows; play-once pending; launch options of user 111
    let st = game::status_with(&dirs, s, &roots).unwrap();
    assert!(!st.running && st.steam_updating && !st.runtime.installed);
    assert!(st.launch_options.as_ref().is_some_and(|l| !l.has_dll_override && l.value.is_none()), "no options for the server");
    write(&c.path.join("kiln/play-once.ini"), "mode = vanilla\n");
    let st = game::status_with(&dirs, c, &roots).unwrap();
    assert!(st.play_once_pending && !st.steam_updating && !st.running);
    assert!(st.launch_options.as_ref().is_some_and(|l| l.has_dll_override && l.advice.is_none()), "user 111 sets the override");

    // launch option facts
    assert!(game::overrides_dinput8_native("mshtml=;dinput8=n,b"));
    assert!(game::overrides_dinput8_native("*=n,b"));
    assert!(!game::overrides_dinput8_native("*=n;dinput8=b"));
    assert!(!game::overrides_dinput8_native("dinput8=b,n"));
    assert_eq!(game::overrides_in_launch_options("A=1 WINEDLLOVERRIDES='dinput8=n' %command%").as_deref(), Some("dinput8=n"));
    assert_eq!(game::overrides_in_launch_options("WINEDLLOVERRIDES=dinput8=n,b %command%").as_deref(), Some("dinput8=n,b"));
    let reg = "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\DllOverrides] 1\n#time=1\n\"dinput8\"=\"native,builtin\"\n";
    assert!(game::user_reg_dinput8_native(reg));
    assert!(!game::user_reg_dinput8_native(&reg.replace("native,builtin", "builtin")));
    let mut plain = c.clone();
    plain.library = None;
    fs::remove_file(f.root.join("userdata/111/config/localconfig.vdf")).unwrap();
    let info = game::launch_options_info(&plain, &roots).unwrap();
    assert_eq!((info.value.as_deref(), info.has_dll_override), (Some("gamemoderun %command%"), false));
    assert_eq!(info.advice.as_deref(), Some(game::DLL_OVERRIDE_ADVICE));
    let pfx = plain.prefix.clone().unwrap();
    write(&pfx.join("user.reg"), reg);
    assert!(game::launch_options_info(&plain, &roots).unwrap().has_dll_override, "the prefix's registry override counts");
    plain.platform = Platform::Windows;
    assert!(game::launch_options_info(&plain, &roots).is_none());
    let _ = fs::remove_dir_all(&f.home);
}

fn install_at(dir: &Path, prefix: Option<PathBuf>, mode: PathMode) -> GameInstall {
    GameInstall {
        id: "g-000000000000".into(),
        kind: GameKind::Client,
        path: dir.to_path_buf(),
        exe: "enshrouded.exe".into(),
        app_id: 1203620,
        library: None,
        platform: if cfg!(windows) { Platform::Windows } else { Platform::Proton },
        prefix,
        build_id: None,
        exe_sha256: None,
        steam_flavor: None,
        path_mode: mode,
        source: "manual".into(),
    }
}

#[cfg(unix)]
#[test]
fn winpath_translations() {
    let t = tmp("winpath");
    let pfx = t.join("pfx");
    fs::create_dir_all(pfx.join("dosdevices")).unwrap();
    fs::create_dir_all(pfx.join("drive_c")).unwrap();
    std::os::unix::fs::symlink("../drive_c", pfx.join("dosdevices/c:")).unwrap();
    std::os::unix::fs::symlink("/", pfx.join("dosdevices/z:")).unwrap();
    std::os::unix::fs::symlink(&t, pfx.join("dosdevices/d:")).unwrap();
    let g = install_at(&t.join("game"), Some(pfx.clone()), PathMode::Z);
    let host = Path::new("/home/u/.local/share/kiln/library/kiln/a/1.0.0+12345678");
    let z = winpath::to_game(host, &g).unwrap();
    assert_eq!(z, "Z:\\home\\u\\.local\\share\\kiln\\library\\kiln\\a\\1.0.0+12345678");
    assert_eq!(winpath::to_host(&z, &g).unwrap(), host);
    assert_eq!(winpath::to_host("C:\\users\\steamuser\\x.txt", &g).unwrap(), pfx.join("drive_c/users/steamuser/x.txt"));
    assert_eq!(winpath::to_host("d:/sub/f.dll", &g).unwrap(), t.join("sub/f.dll"));
    assert_eq!(winpath::to_host("profiles/p/kiln/m", &g).unwrap(), t.join("game/kiln/profiles/p/kiln/m"));
    assert_eq!(winpath::to_host("chain\\emf-dinput8.dll", &g).unwrap(), t.join("game/kiln/chain/emf-dinput8.dll"));
    assert_eq!(winpath::to_host("..\\x", &g).unwrap_err().code, ErrorCode::InvalidArgument);
    assert_eq!(winpath::to_host("Q:\\x", &g).unwrap_err().code, ErrorCode::NotFound);
    assert_eq!(winpath::to_host("\\\\server\\share\\x.dll", &g).unwrap_err().code, ErrorCode::Unsupported);
    assert_eq!(winpath::to_game(Path::new("/a/b?c"), &g).unwrap_err().code, ErrorCode::Unsupported);
    let copy = install_at(&t.join("game"), None, PathMode::Copy);
    assert_eq!(winpath::to_game(host, &copy).unwrap_err().code, ErrorCode::Unsupported);
    // without a prefix: Z: is /, C: has no target
    assert_eq!(winpath::to_host("Z:\\x\\y", &copy).unwrap(), Path::new("/x/y"));
    assert_eq!(winpath::to_host("C:\\x", &copy).unwrap_err().code, ErrorCode::NotFound);
    assert!(winpath::is_absolute_game_path("C:/x") && winpath::is_absolute_game_path("\\\\s\\x") && !winpath::is_absolute_game_path("chain\\x.dll"));
    let _ = fs::remove_dir_all(&t);
}

#[cfg(windows)]
#[test]
fn winpath_native_mode() {
    let g = install_at(Path::new(r"C:\Games\Enshrouded"), None, PathMode::Native);
    assert_eq!(winpath::to_game(Path::new(r"C:\Users\u\AppData\Local\Kiln\library"), &g).unwrap(), r"C:\Users\u\AppData\Local\Kiln\library");
    assert_eq!(winpath::to_game(Path::new(r"\\?\C:\x"), &g).unwrap(), r"C:\x");
    assert_eq!(winpath::to_game(Path::new(r"\\?\UNC\nas\games\Kiln"), &g).unwrap(), r"\\nas\games\Kiln");
    assert_eq!(winpath::to_game(Path::new(r"C:\Users\Jörg Müller\AppData\Local\Kiln"), &g).unwrap(), r"C:\Users\Jörg Müller\AppData\Local\Kiln");
    assert_eq!(winpath::to_host(r"D:\mods\a.dll", &g).unwrap(), Path::new(r"D:\mods\a.dll"));
    // Z: is a real drive on Windows (a mapped share): never read as the Unix root
    assert_eq!(winpath::to_host(r"Z:\Games\Kiln\mods\a", &g).unwrap(), Path::new(r"Z:\Games\Kiln\mods\a"));
    assert!(winpath::z_to_unix(r"Z:\Games").is_none());
    assert_eq!(winpath::to_host("profiles/p/kiln/m", &g).unwrap(), Path::new(r"C:\Games\Enshrouded\kiln\profiles\p\kiln\m"));
}

/// A fake client with saves: Steam userdata (two users) and the prefix's local settings.
#[cfg_attr(windows, allow(dead_code))]
fn fake_saves(name: &str) -> (PathBuf, GameInstall, Vec<SteamRoot>) {
    let t = tmp(name);
    let root = t.join("Steam");
    fs::create_dir_all(root.join("steamapps")).unwrap();
    let remote = root.join("userdata/111/1203620/remote");
    write(&remote.join("character0.ksd"), b"SV2C character zero");
    write(&remote.join("world0.ksd"), b"SV2C world zero");
    write(&remote.join("global.ksd"), b"SV2C global");
    write(&remote.join("backup/character0.ksd.backup-0"), b"SV2C old");
    write(&root.join("userdata/222/1203620/remote/character1.ksd"), b"SV2C other user");
    fs::create_dir_all(root.join("userdata/333/config")).unwrap(); // a user without saves
    let pfx = t.join("pfx");
    write(&pfx.join("drive_c/users/steamuser/Saved Games/keen games/Enshrouded/local_settings.json"), b"{}");
    let game_dir = t.join("Enshrouded");
    write(&game_dir.join("enshrouded.exe"), b"MZ");
    let mut g = install_at(&game_dir, Some(pfx), PathMode::Copy);
    g.platform = Platform::Proton;
    (t, g, vec![SteamRoot { path: root, flavor: SteamFlavor::Native }])
}

#[cfg(unix)]
#[test]
fn saves_list_backup_prune_restore() {
    let (t, g, roots) = fake_saves("saves");
    let dirs = DataDirs::at(t.join("data"));
    let sd = saves::save_dirs_in(&g, &roots);
    assert_eq!(sd.iter().map(|d| d.label.as_str()).collect::<Vec<_>>(), vec!["steam-111", "steam-222", "local"]);
    let slots = saves::list_in(&sd).unwrap();
    let names: Vec<(&str, saves::SlotKind)> = slots.iter().map(|s| (s.file.as_str(), s.kind)).collect();
    assert_eq!(names, vec![
        ("character0.ksd", saves::SlotKind::Character),
        ("global.ksd", saves::SlotKind::Other),
        ("world0.ksd", saves::SlotKind::World),
        ("character1.ksd", saves::SlotKind::Character),
        ("local_settings.json", saves::SlotKind::Other),
    ], "the game's own backup/ folder is not a slot");
    assert_eq!(slots[0].steam_id.as_deref(), Some("111"));

    let b = saves::backup_from(&dirs, &g, &sd, saves::BackupReason::Launch, Some("first"), Some("kms1:abc")).unwrap();
    assert_eq!((b.files, b.reason, b.fingerprint.as_deref(), b.profile_id.clone()), (5, saves::BackupReason::Launch, Some("kms1:abc"), None));
    let folder = dirs.backups().join("saves").join(&g.id).join(&b.id);
    assert!(folder.join("backup.json").is_file() && folder.join("steam-111/character0.ksd").is_file());
    assert!(folder.join("local/local_settings.json").is_file());

    // restore: byte-identical, and the current state is backed up first (reason restore)
    let remote = roots[0].path.join("userdata/111/1203620/remote");
    fs::write(remote.join("character0.ksd"), b"SV2C changed by a bad mod").unwrap();
    fs::remove_file(remote.join("world0.ksd")).unwrap();
    saves::restore_to(&dirs, &g, &sd, &b.id).unwrap();
    assert_eq!(fs::read(remote.join("character0.ksd")).unwrap(), b"SV2C character zero");
    assert_eq!(fs::read(remote.join("world0.ksd")).unwrap(), b"SV2C world zero");
    let all = saves::backups(&dirs, &g).unwrap();
    assert_eq!(all.len(), 2);
    assert_eq!(all[0].reason, saves::BackupReason::Restore, "newest first");
    assert_eq!(saves::restore_to(&dirs, &g, &sd, "nope").unwrap_err().code, ErrorCode::NotFound);

    // a damaged backup is refused before anything is touched
    fs::write(folder.join("steam-111/world0.ksd"), b"bit rot").unwrap();
    assert_eq!(saves::restore_to(&dirs, &g, &sd, &b.id).unwrap_err().code, ErrorCode::HashMismatch);
    assert_eq!(fs::read(remote.join("world0.ksd")).unwrap(), b"SV2C world zero");

    // keep 10 automatic, never prune manual
    let manual = saves::backup_from(&dirs, &g, &sd, saves::BackupReason::Manual, Some("keep me"), None).unwrap();
    for _ in 0..12 {
        saves::backup_from(&dirs, &g, &sd, saves::BackupReason::Launch, None, None).unwrap();
    }
    let all = saves::backups(&dirs, &g).unwrap();
    assert_eq!(all.iter().filter(|b| b.reason != saves::BackupReason::Manual).count(), saves::KEEP);
    assert!(all.iter().any(|b| b.id == manual.id));
    assert!(!all.iter().any(|x| x.id == b.id), "the oldest launch backup was pruned");
    saves::delete(&dirs, &g, &manual.id).unwrap();
    assert!(!saves::backups(&dirs, &g).unwrap().iter().any(|b| b.id == manual.id));

    // an active profile puts new backups into the profile's save-backups/ (still listed for the game)
    write(&g.path.join("kiln/profile.json"), r#"{"format":1,"id":"ember-run","generation":1,"mods":{}}"#);
    let pb = saves::backup_from(&dirs, &g, &sd, saves::BackupReason::Modpack, None, None).unwrap();
    if pb.profile_id.is_some() {
        // profile::read_game_profile is implemented: the backup went to the profile
        assert!(dirs.profile("ember-run").join("save-backups").join(&pb.id).join("backup.json").is_file());
    }
    let ids: Vec<String> = saves::backups(&dirs, &g).unwrap().into_iter().map(|b| format!("{} {} {:?}", b.id, b.created, b.reason)).collect();
    assert!(ids.iter().any(|b| b.starts_with(&format!("{} ", pb.id))), "{pb:?} not in {ids:#?}");
    // servers keep their saves in the server folder: none here
    let mut s = g.clone();
    s.kind = GameKind::Server;
    assert!(saves::save_dirs_in(&s, &roots).is_empty());
    let _ = fs::remove_dir_all(&t);
}

/// Runs a copy of `sleep` named enshrouded.exe in the game folder (comm + cwd: what a Wine game looks like).
#[cfg(target_os = "linux")]
fn fake_game_process(dir: &Path) -> std::process::Child {
    let exe = dir.join("enshrouded.exe");
    fs::copy("/bin/sleep", &exe).unwrap();
    std::process::Command::new(&exe).arg("30").current_dir(dir).spawn().unwrap()
}

#[cfg(target_os = "linux")]
#[test]
fn running_game_blocks_restore_and_is_seen_by_launch() {
    let (t, g, roots) = fake_saves("running");
    let dirs = DataDirs::at(t.join("data"));
    let sd = saves::save_dirs_in(&g, &roots);
    let b = saves::backup_from(&dirs, &g, &sd, saves::BackupReason::Manual, None, None).unwrap();
    assert!(launch::wait_started(&g, std::time::Duration::from_millis(50), std::time::Duration::from_millis(10)).unwrap().is_none());
    let mut child = fake_game_process(&g.path);
    let seen = launch::wait_started(&g, std::time::Duration::from_secs(5), std::time::Duration::from_millis(20)).unwrap();
    let ok = || {
        assert_eq!(seen.as_ref().map(|p| p.pid), Some(child.id()));
        assert_eq!(saves::restore_to(&dirs, &g, &sd, &b.id).unwrap_err().code, ErrorCode::GameRunning);
        // boot.json written after the process started counts as current; its nonce must match when asked
        let boot = fs::read_to_string(repo().join("schema/examples/state-boot.json")).unwrap();
        write(&g.path.join("kiln/state/boot.json"), &boot);
        let b = launch::read_boot_for(&g, None).unwrap();
        assert!(b.is_some());
        let nonce = b.unwrap().play_once.and_then(|p| p.nonce);
        if let Some(n) = nonce {
            assert!(launch::read_boot_for(&g, Some(&n)).unwrap().is_some());
        }
        assert!(launch::read_boot_for(&g, Some("other-nonce")).unwrap().is_none());
    };
    let r = std::panic::catch_unwind(ok);
    let _ = child.kill();
    let _ = child.wait();
    r.unwrap();
    // the game is gone: the boot file is stale
    assert!(launch::read_boot_for(&g, None).unwrap().is_none());
    saves::restore_to(&dirs, &g, &sd, &b.id).unwrap();
    let _ = fs::remove_dir_all(&t);
}

#[test]
fn open_uri_validates_and_uses_the_opener() {
    let seen = std::cell::RefCell::new(Vec::new());
    let rec = |u: &str| {
        seen.borrow_mut().push(u.to_string());
        Ok(())
    };
    launch::open_uri_with(&launch::steam_uri(1203620), &rec).unwrap();
    assert_eq!(seen.borrow().as_slice(), ["steam://rungameid/1203620"]);
    for bad in ["https://example.com", "steam://run 1", "steam://x\"&calc", "file:///etc/passwd", ""] {
        assert_eq!(launch::open_uri_with(bad, &rec).unwrap_err().code, ErrorCode::InvalidArgument, "{bad}");
    }
    assert_eq!(seen.borrow().len(), 1);
}

#[test]
fn logs_list_read_tail_and_crash_reports() {
    let t = tmp("logs");
    let log = "[I 10:00:00.000] [kiln] Kiln 0.2.0-dev starting (pid 7)\r\n[I 10:00:00.100] [kiln] boot complete; handing control to the game\r\n[W 10:00:01.000] [mod:x] partial";
    write(&t.join("kiln/logs/kiln.log"), log);
    write(&t.join("kiln/logs/proxy.log"), "Kiln proxy\n");
    write(&t.join("kiln/logs/tests/x.json"), "{}");
    write(&t.join("kiln/logs/.hidden"), "x");
    write(&t.join("kiln/crash-reports/crash-1.txt"), "boom");
    write(&t.join("enshrouded.log"), "game\r\n");
    let names: Vec<String> = logs::list(&t).unwrap().into_iter().map(|l| l.name).collect();
    assert_eq!(names, ["kiln/logs/kiln.log", "kiln/logs/proxy.log", "kiln/logs/tests/x.json", "kiln/crash-reports/crash-1.txt", "enshrouded.log"]);
    let all = logs::read(&t, "kiln/logs/kiln.log", Some(0), None).unwrap();
    assert_eq!(all.text, "[I 10:00:00.000] [kiln] Kiln 0.2.0-dev starting (pid 7)\n[I 10:00:00.100] [kiln] boot complete; handing control to the game\n");
    assert!(!all.text.contains('\r'));
    assert_eq!(all.next, log.rfind('\n').unwrap() as u64 + 1, "the partial last line waits");
    let tail = logs::read(&t, "kiln/logs/kiln.log", None, Some(120)).unwrap();
    assert!(tail.truncated && tail.text.starts_with("[I 10:00:00.100]"), "{tail:?}");
    let small = logs::read(&t, "kiln/logs/kiln.log", Some(0), Some(60)).unwrap();
    assert!(small.truncated && small.text.lines().count() == 1);
    let next = logs::read(&t, "kiln/logs/kiln.log", Some(small.next), Some(1 << 20)).unwrap();
    assert!(next.text.starts_with("[I 10:00:00.100]"));
    let past = logs::read(&t, "kiln/logs/kiln.log", Some(1 << 30), None).unwrap();
    assert_eq!(past.offset, 0, "an offset past the end (truncated file) starts over");
    assert_eq!(logs::read(&t, "../etc/passwd", None, None).unwrap_err().code, ErrorCode::InvalidArgument);
    assert_eq!(logs::read(&t, "kiln/logs/none.log", None, None).unwrap_err().code, ErrorCode::NotFound);
    let crashes = logs::crash_reports(&t).unwrap();
    assert_eq!((crashes.len(), crashes[0].name.as_str(), crashes[0].size), (1, "kiln/crash-reports/crash-1.txt", 4));
    let b = logs::boot_state(&String::from_utf8_lossy(&fs::read(t.join("kiln/logs/kiln.log")).unwrap()).replace('\r', ""));
    assert!(b.boot_complete && b.pid == Some(7) && b.warnings == 1);
    let _ = fs::remove_dir_all(&t);
}

#[test]
fn follower_handles_partial_lines_truncation_and_replacement() {
    let t = tmp("follow");
    let p = t.join("kiln.log");
    fs::write(&p, "old\r\n").unwrap();
    let mut f = logs::Follower::open(&p).unwrap();
    assert!(f.poll().unwrap().is_empty(), "starts at the end");
    let append = |s: &str| {
        use std::io::Write;
        let mut h = fs::OpenOptions::new().append(true).open(&p).unwrap();
        h.write_all(s.as_bytes()).unwrap();
    };
    append("one\r\ntw");
    assert_eq!(f.poll().unwrap(), ["one"]);
    append("o\r\n");
    assert_eq!(f.poll().unwrap(), ["two"]);
    // truncated and rewritten (a new boot)
    fs::write(&p, "boot\n").unwrap();
    assert_eq!(f.poll().unwrap(), ["boot"]);
    // replaced by a new file (rename over)
    let n = t.join("kiln.log.new");
    fs::write(&n, "fresh 1\nfresh 2\n").unwrap();
    fs::rename(&n, &p).unwrap();
    assert_eq!(f.poll().unwrap(), ["fresh 1", "fresh 2"]);
    assert_eq!(f.offset(), 16);
    // from an offset, and a file that does not exist yet
    let mut g = logs::Follower::open_at(&p, 8).unwrap();
    assert_eq!(g.poll().unwrap(), ["fresh 2"]);
    let mut later = logs::Follower::open(&t.join("later.log")).unwrap();
    assert!(later.poll().unwrap().is_empty());
    fs::write(t.join("later.log"), "hello\n").unwrap();
    assert_eq!(later.poll().unwrap(), ["hello"]);
    let _ = fs::remove_dir_all(&t);
}

/// Windows (under Wine): `steam::roots` reads HKCU\Software\Valve\Steam SteamPath. The test harness sets
/// `KILN_TEST_STEAMPATH` to the fake root it registered with `wine reg add`.
#[cfg(windows)]
#[test]
fn windows_registry_root() {
    let Some(want) = std::env::var_os("KILN_TEST_STEAMPATH") else {
        eprintln!("KILN_TEST_STEAMPATH not set: registry check skipped");
        return;
    };
    let roots = steam::roots().unwrap();
    let want = steam::canon(Path::new(&want));
    // SteamPath is written in lower case with '/' (c:/program files (x86)/steam); the folder is the same either way
    assert!(roots.iter().any(|r| steam::same_path(&r.path, &want) && r.flavor == SteamFlavor::Windows), "{roots:?} vs {}", want.display());
    assert!(roots.iter().all(|r| !r.path.to_string_lossy().contains('/')), "{roots:?}");
    assert_eq!(roots.iter().filter(|r| steam::same_path(&r.path, &want)).count(), 1, "one root per folder: {roots:?}");
}

/// Windows paths are case-insensitive: one folder, one install id, whatever the spelling; and a root found in the
/// registry's lower case (`c:/program files (x86)/steam`) takes libraryfolders.vdf's real spelling.
#[cfg(windows)]
#[test]
fn windows_paths_are_case_insensitive() {
    let t = steam::strip_verbatim(tmp("wincase"));
    let dir = t.join("Steam Bibliothek Ü").join("steamapps").join("common").join("Enshrouded");
    fs::create_dir_all(&dir).unwrap();
    let lower = PathBuf::from(dir.to_string_lossy().to_lowercase());
    assert_eq!(game::path_install_id(&dir), game::path_install_id(&lower));
    let root = t.join("Steam Bibliothek Ü");
    let vdf_path = root.display().to_string().replace('\\', "\\\\");
    write(&root.join("steamapps/libraryfolders.vdf"), format!("\"libraryfolders\" {{ \"0\" {{ \"path\" \"{vdf_path}\" }} }}"));
    let lower_root = PathBuf::from(root.to_string_lossy().to_lowercase());
    let libs = steam::libraries(&SteamRoot { path: lower_root, flavor: SteamFlavor::Windows }).unwrap();
    assert_eq!(libs.len(), 1, "{libs:?}");
    assert!(libs[0].path.to_string_lossy().contains("Steam Bibliothek Ü"), "{libs:?}");
    let _ = fs::remove_dir_all(&t);
}

#[cfg(windows)]
#[test]
fn windows_detect_and_saves() {
    let f = fake_steam("windetect");
    let roots = vec![root_of(&f)];
    assert_eq!(roots[0].flavor, SteamFlavor::Windows);
    let found = game::detect_in(&roots, Path::new(r"C:\Users\u\AppData\Local\Kiln")).unwrap();
    assert_eq!(found.len(), 2, "{found:#?}");
    assert_eq!((found[0].platform, found[0].path_mode, found[0].prefix.clone()), (Platform::Windows, PathMode::Native, None));
    assert_eq!(found[0].path, f.client);
    let lo = steam::launch_options(&roots[0], 1203620).unwrap();
    assert_eq!(lo.len(), 2);
    // saves from Steam userdata
    let remote = f.root.join("userdata/111/1203620/remote");
    write(&remote.join("character0.ksd"), b"SV2C a");
    let g = &found[0];
    let sd = saves::save_dirs_in(g, &roots);
    assert!(sd.iter().any(|d| d.label == "steam-111"));
    let dirs = DataDirs::at(f.home.join("data"));
    let b = saves::backup_from(&dirs, g, &sd, saves::BackupReason::Manual, None, None).unwrap();
    fs::write(remote.join("character0.ksd"), b"SV2C b").unwrap();
    saves::restore_to(&dirs, g, &sd, &b.id).unwrap();
    assert_eq!(fs::read(remote.join("character0.ksd")).unwrap(), b"SV2C a");
    let _ = fs::remove_dir_all(&f.home);
}

/// Read-only smoke run against this machine's Steam (`cargo test --test discovery -- --ignored --nocapture`).
#[test]
#[ignore]
fn real_steam_read_only_smoke() {
    for r in steam::roots().unwrap() {
        println!("root {} {:?}", r.path.display(), r.flavor);
        for l in steam::libraries(&r).unwrap() {
            println!("  library {} apps {:?}", l.path.display(), l.apps);
        }
        println!("  compat 1203620 {:?}, 2278520 {:?}", steam::compat_tool(&r, 1203620), steam::compat_tool(&r, 2278520));
        println!("  launch options {:?}", steam::launch_options(&r, 1203620));
    }
    for g in game::detect().unwrap() {
        println!("{} {:?} {} build {:?} {:?} prefix {:?} mode {:?}", g.id, g.kind, g.path.display(), g.build_id, g.platform, g.prefix, g.path_mode);
        println!("  launch options {:?}", game::launch_options_info(&g, &steam::roots().unwrap()));
        println!("  saves {}", saves::list(&g).map(|v| v.len()).unwrap_or(0));
    }
}
