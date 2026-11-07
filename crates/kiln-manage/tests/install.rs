//! Runtime bundles and the installer on fake game folders (temp dirs): determinism, verify, zip-slip, proxy
//! classification table, chain entry + backup + receipt, upgrade, uninstall byte-identical, rollback.
#![cfg(feature = "zip")]

use kiln_manage::bundle::{self, CreateOptions};
use kiln_manage::game::{GameInstall, GameKind, PathMode, Platform};
use kiln_manage::install::{self, InstallOptions};
use kiln_manage::pe::build_test_dll;
use kiln_manage::task::NoProgress;
use kiln_manage::{ErrorCode, chain, hash, paths::DataDirs};
use std::path::{Path, PathBuf};

struct Tmp(PathBuf);
impl Drop for Tmp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
fn tmp(tag: &str) -> Tmp {
    let p = std::env::temp_dir().join(format!("km-install-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&p);
    std::fs::create_dir_all(&p).unwrap();
    Tmp(p)
}

fn put(root: &Path, rel: &str, data: &[u8]) {
    let p = root.join(rel);
    std::fs::create_dir_all(p.parent().unwrap()).unwrap();
    std::fs::write(p, data).unwrap();
}

fn kiln_proxy(version: &str) -> Vec<u8> {
    build_test_dll(&["DirectInput8Create", "GetdfDIJoystick", "KilnProxyInfo"], format!("KilnProxy/2 {version}\0").as_bytes())
}

/// A staging folder like tools/package/stage.sh writes.
fn stage(root: &Path, version_tag: &str, with_old_file: bool) -> PathBuf {
    let s = root.join(format!("stage-{version_tag}"));
    put(&s, "kiln-runtime.json", br#"{
  "format": 1, "kiln": "0.2.0-dev", "abi": 2, "api": "0.2.0", "dotnet": "10.0.14",
  "components": {
    "core": { "sides": ["server", "client"], "default": true, "description": "proxy, core, host" },
    "sigdb-server": { "sides": ["server"], "default": true, "requires": ["core"] },
    "sigdb-client": { "sides": ["client"], "default": true, "requires": ["core"] },
    "emf": { "sides": ["server", "client"], "default": false, "requires": ["core"] }
  },
  "builds": { "client": [ { "sha256": "ce6fdba63e27744943b695921383a29133285d359e07671f6ca33a47e2147618", "steamBuild": "26404966" } ] },
  "config": ["kiln/kiln.json"]
}"#);
    put(&s, "core/dinput8.dll", &kiln_proxy(&format!("0.2.0-dev+{version_tag}")));
    put(&s, "core/kiln/kiln-core.dll", format!("core {version_tag}").as_bytes());
    put(&s, "core/kiln/kiln.json", b"{\n  \"policy\": \"verified-only\"\n}\n");
    put(&s, "core/kiln/host/Kiln.Host.dll", b"host");
    if with_old_file {
        put(&s, "core/kiln/services/kiln-old.dll", b"old service");
        put(&s, "core/kiln/services/kiln-edited.dll", b"edited service");
    }
    put(&s, "sigdb-client/kiln/sigdb/client.json", b"{\"symbols\":{}}");
    put(&s, "sigdb-server/kiln/sigdb/server.json", b"{\"symbols\":{}}");
    put(&s, "emf/kiln/plugins/kiln-emf.dll", b"emf plugin");
    s
}

fn client(dir: &Path) -> GameInstall {
    GameInstall {
        id: "g-test".into(),
        kind: GameKind::Client,
        path: dir.to_path_buf(),
        exe: "enshrouded.exe".into(),
        app_id: kiln_manage::CLIENT_APP_ID,
        library: None,
        platform: Platform::Proton,
        prefix: None,
        build_id: Some("26404966".into()),
        exe_sha256: None,
        steam_flavor: None,
        path_mode: PathMode::Copy,
        source: "manual".into(),
    }
}

fn opts(epoch: i64, git: &str) -> CreateOptions {
    CreateOptions { source_date_epoch: epoch, git: git.into() }
}

/// Every file below `dir` except kiln/ -> sha256 (what "the original game folder" means for uninstall).
fn outside_kiln(dir: &Path) -> Vec<(String, String)> {
    let mut v = Vec::new();
    for e in std::fs::read_dir(dir).unwrap().flatten() {
        let n = e.file_name().to_string_lossy().into_owned();
        if n != "kiln" && e.file_type().unwrap().is_file() {
            v.push((n, hash::sha256_file(&e.path()).unwrap().0));
        }
    }
    v.sort();
    v
}

#[test]
fn bundles_are_deterministic_and_verified() {
    let t = tmp("det");
    let s = stage(&t.0, "a", false);
    let a = bundle::create(&s, &t.0.join("out1"), &opts(1_793_793_600, "0f1ffa6")).unwrap();
    let b = bundle::create(&s, &t.0.join("out2"), &opts(1_793_793_600, "0f1ffa6")).unwrap();
    assert_eq!(a.file_name().unwrap(), "kiln-runtime-0.2.0-dev+20261104.0f1ffa6.zip");
    assert_eq!(std::fs::read(&a).unwrap(), std::fs::read(&b).unwrap(), "two runs give byte-identical zips");
    let m = bundle::verify(&a, &mut NoProgress).unwrap();
    assert_eq!(m.version, "0.2.0-dev+20261104.0f1ffa6");
    assert_eq!(m.created.as_deref(), Some("2026-11-04T12:00:00Z"));
    assert!(m.files.windows(2).all(|w| w[0].path < w[1].path));
    assert!(m.files.iter().find(|f| f.path == "kiln/kiln.json").unwrap().config);
    assert_eq!(bundle::sides(&m).len(), 2);
    // client plan: core + sigdb-client, no server sigdb, no emf (not default)
    let plan: Vec<String> = install::plan(&m, kiln_manage::manifest::RunSide::Client, None).unwrap().into_iter().map(|f| f.path).collect();
    assert!(plan.contains(&"kiln/sigdb/client.json".into()) && !plan.contains(&"kiln/sigdb/server.json".into()));
    assert!(!plan.contains(&"kiln/plugins/kiln-emf.dll".into()));
    assert!(install::plan(&m, kiln_manage::manifest::RunSide::Client, Some(&["sigdb-server".into()])).is_err());
    // a tampered entry fails verification: same manifest, other bytes for one file
    let tampered = t.0.join("tampered.zip");
    {
        use std::io::{Read, Write};
        let mut src = zip::ZipArchive::new(std::fs::File::open(&a).unwrap()).unwrap();
        let mut w = zip::ZipWriter::new(std::fs::File::create(&tampered).unwrap());
        for i in 0..src.len() {
            let mut e = src.by_index(i).unwrap();
            let name = e.name().unwrap().to_string();
            let mut data = Vec::new();
            e.read_to_end(&mut data).unwrap();
            if name == "kiln/kiln-core.dll" {
                data[0] ^= 1;
            }
            w.start_file(name, zip::write::SimpleFileOptions::default()).unwrap();
            w.write_all(&data).unwrap();
        }
        w.finish().unwrap();
    }
    assert_eq!(bundle::verify(&tampered, &mut NoProgress).unwrap_err().code, ErrorCode::HashMismatch);
    // import and list
    let dirs = DataDirs::at(t.0.join("data"));
    let imported = bundle::import(&dirs, &a).unwrap();
    assert_eq!(imported.version, m.version);
    assert_eq!(bundle::list(&dirs).unwrap().len(), 1);
}

#[test]
fn a_shipped_bundle_is_copied_once_and_a_damaged_copy_is_replaced() {
    let t = tmp("shipped");
    let zip = bundle::create(&stage(&t.0, "a", false), &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    let dirs = DataDirs::at(t.0.join("data"));

    let first = bundle::import_shipped(&dirs, &zip).unwrap().expect("first start: copied");
    assert_eq!(bundle::list(&dirs).unwrap().len(), 1);
    assert!(bundle::import_shipped(&dirs, &zip).unwrap().is_none(), "second start: nothing to do");

    let copy = dirs.runtime().join(bundle::file_name(&first.version));
    std::fs::write(&copy, b"not a zip").unwrap();
    assert!(bundle::list(&dirs).unwrap().is_empty());
    assert!(bundle::import_shipped(&dirs, &zip).unwrap().is_some(), "a damaged copy comes back");
    assert_eq!(bundle::list(&dirs).unwrap()[0].version, first.version);

    std::fs::write(t.0.join("bad.zip"), b"not a zip").unwrap();
    assert!(bundle::import_shipped(&dirs, &t.0.join("bad.zip")).is_err());
}

#[test]
fn unsafe_zip_entries_are_refused() {
    use std::io::Write;
    let t = tmp("slip");
    let z = t.0.join("evil.zip");
    let mut w = zip::ZipWriter::new(std::fs::File::create(&z).unwrap());
    let o = zip::write::SimpleFileOptions::default();
    let manifest = r#"{"format":1,"version":"0.2.0-dev+20261104.0f1ffa6","kiln":"0.2.0-dev","abi":2,"components":{"core":{"sides":["client"]}},"files":[{"path":"../evil.dll","size":1,"sha256":"ca978112ca1bbdcafac231b39a23dc4da786eff8147c4e72b9807785afee48bb","component":"core"}]}"#;
    w.start_file("kiln-runtime.json", o).unwrap();
    w.write_all(manifest.as_bytes()).unwrap();
    w.start_file("../evil.dll", o).unwrap();
    w.write_all(b"a").unwrap();
    w.finish().unwrap();
    let e = bundle::verify(&z, &mut NoProgress).unwrap_err();
    assert!(matches!(e.code, ErrorCode::Refused | ErrorCode::Schema), "{e}");
    for bad in ["", "/abs", "a/../b", "a\\b", "C:/x", "a//b", "./a"] {
        assert!(bundle::safe_rel_path(bad).is_err(), "{bad:?}");
    }
    assert!(bundle::safe_rel_path("kiln/dotnet/host/fxr/10.0.14/hostfxr.dll").is_ok());
}

#[test]
fn install_chains_emf_backs_up_and_uninstall_restores_it() {
    let t = tmp("emf");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("Enshrouded");
    put(&game, "enshrouded.exe", b"MZ fake game");
    let emf = build_test_dll(&["DirectInput8Create", "emf_loader_identity", "emf_loader_play_once"], b"EMF loader");
    put(&game, "dinput8.dll", &emf);
    put(&game, "emf/mods/beastmaster/mod.json", b"{}");
    let before = outside_kiln(&game);
    let b1 = bundle::create(&stage(&t.0, "a", true), &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    let inst = client(&game);
    let r = install::install(&dirs, &inst, &b1, &InstallOptions::default(), &mut NoProgress).unwrap();
    // chain entry, backup, receipt
    let c = chain::read(&game).unwrap();
    assert_eq!(c.chain.iter().map(|e| e.path.as_str()).collect::<Vec<_>>(), ["chain\\emf-dinput8.dll"]);
    assert_eq!(std::fs::read(game.join("kiln/chain/emf-dinput8.dll")).unwrap(), emf);
    let rep = &r.replaced[0];
    assert_eq!((rep.path.as_str(), rep.action, rep.class), ("dinput8.dll", install::ReplacedAction::Chained, Some(install::ReplacedClass::Emf)));
    assert_eq!(std::fs::read(game.join(rep.backup.as_ref().unwrap())).unwrap(), emf);
    assert_eq!(rep.sha256, hash::sha256_hex(&emf));
    let mirrored = dirs.backups().join("install").join(&r.install_id);
    assert!(std::fs::read_dir(&mirrored).unwrap().count() == 1);
    assert_eq!(kiln_manage::pe::classify_proxy(&game.join("dinput8.dll")).unwrap().kind, kiln_manage::pe::ProxyKind::Kiln);
    let on_disk = install::read_receipt(&game).unwrap().unwrap();
    assert_eq!(on_disk, r);
    assert_eq!(r.side, kiln_manage::manifest::RunSide::Client);
    assert!(r.files.iter().any(|f| f.path == "kiln/sigdb/client.json") && !r.files.iter().any(|f| f.path == "kiln/sigdb/server.json"));
    assert!(game.join("kiln/sigdb/builds/client").is_dir() && game.join("kiln/logs").is_dir());
    assert!(!std::fs::read_dir(game.join("kiln")).unwrap().flatten().any(|e| e.file_name().to_string_lossy().starts_with(".staging")));
    assert!(dirs.installs().join(format!("{}.json", r.install_id)).exists());
    let st = install::status(&dirs, &inst).unwrap();
    assert!(st.installed && st.modified.is_empty());
    // the user edits two things: kiln.json (config) and a service DLL
    std::fs::write(game.join("kiln/kiln.json"), b"{\"policy\": \"any\"}").unwrap();
    std::fs::write(game.join("kiln/services/kiln-edited.dll"), b"patched by hand").unwrap();
    assert_eq!(install::status(&dirs, &inst).unwrap().modified, ["kiln/services/kiln-edited.dll"]);

    // upgrade: a newer bundle without the two service DLLs
    let b2 = bundle::create(&stage(&t.0, "b", false), &t.0.join("out"), &opts(1_793_880_000, "bbbbbbb")).unwrap();
    let r2 = install::install(&dirs, &inst, &b2, &InstallOptions::default(), &mut NoProgress).unwrap();
    assert_eq!(r2.install_id, r.install_id);
    assert_eq!(r2.installed, r.installed);
    assert!(r2.updated.is_some());
    assert_eq!(std::fs::read(game.join("kiln/kiln-core.dll")).unwrap(), b"core b");
    assert!(!game.join("kiln/services/kiln-old.dll").exists(), "an unchanged obsolete file goes");
    assert_eq!(std::fs::read(game.join("kiln/services/kiln-edited.dll")).unwrap(), b"patched by hand", "a changed one stays");
    assert_eq!(std::fs::read(game.join("kiln/kiln.json")).unwrap(), b"{\"policy\": \"any\"}", "config files are never replaced");
    assert_eq!(r2.replaced.len(), 1, "the upgrade replaced only its own files");
    assert_eq!(chain::read(&game).unwrap().chain.len(), 1);

    // uninstall: the original dinput8.dll is back byte-identically, kiln/ stays
    install::uninstall(&dirs, &inst, &mut NoProgress).unwrap();
    assert_eq!(outside_kiln(&game), before);
    assert_eq!(std::fs::read(game.join("dinput8.dll")).unwrap(), emf);
    assert!(game.join("kiln/kiln.json").exists() && game.join("kiln/backups").is_dir());
    assert!(!game.join("kiln/kiln-core.dll").exists() && !game.join("kiln/.install.json").exists());
    assert!(chain::read(&game).unwrap().chain.is_empty());
    assert!(game.join("emf/mods/beastmaster/mod.json").exists());

    // rollback = an older bundle from <data>/runtime
    bundle::import(&dirs, &b1).unwrap();
    bundle::import(&dirs, &b2).unwrap();
    install::install(&dirs, &inst, &b2, &InstallOptions::default(), &mut NoProgress).unwrap();
    let rb = install::rollback(&dirs, &inst, "0.2.0-dev+20261104.aaaaaaa", &mut NoProgress).unwrap();
    assert_eq!(rb.runtime.version, "0.2.0-dev+20261104.aaaaaaa");
    assert_eq!(std::fs::read(game.join("kiln/kiln-core.dll")).unwrap(), b"core a");
    install::uninstall(&dirs, &inst, &mut NoProgress).unwrap();
    assert_eq!(outside_kiln(&game), before);
}

#[test]
fn an_upgrade_reports_the_hand_changed_files_it_backed_up() {
    let t = tmp("handchanged");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("Enshrouded");
    put(&game, "enshrouded.exe", b"MZ fake game");
    let inst = client(&game);
    let b1 = bundle::create(&stage(&t.0, "a", false), &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    let (r1, rep1) = install::install_report(&dirs, &inst, &b1, &InstallOptions::default(), &mut NoProgress).unwrap();
    assert!(rep1.changed_backed_up.is_empty() && rep1.backup.is_none(), "a first install into a clean folder backs nothing up: {rep1:?}");
    // a Kiln file and the config are edited by hand; the next install replaces the file and never the config
    std::fs::write(game.join("kiln/kiln-core.dll"), b"patched by hand").unwrap();
    std::fs::write(game.join("kiln/kiln.json"), b"{\"policy\": \"any\"}").unwrap();
    let b2 = bundle::create(&stage(&t.0, "b", false), &t.0.join("out"), &opts(1_793_880_000, "bbbbbbb")).unwrap();
    let (r2, rep2) = install::install_report(&dirs, &inst, &b2, &InstallOptions::default(), &mut NoProgress).unwrap();
    assert_eq!(rep2.changed_backed_up, ["kiln/kiln-core.dll"]);
    let mirror = PathBuf::from(rep2.backup.as_deref().expect("the backup folder"));
    assert!(mirror.starts_with(dirs.backups().join("install").join(&r1.install_id)), "{}", mirror.display());
    assert_eq!(std::fs::read(mirror.join("kiln/kiln-core.dll")).unwrap(), b"patched by hand");
    assert_eq!(std::fs::read(game.join("kiln/kiln-core.dll")).unwrap(), b"core b");
    assert_eq!(r2.install_id, r1.install_id);
    // the report is not part of the receipt file
    let on_disk = std::fs::read_to_string(game.join("kiln/.install.json")).unwrap();
    assert!(!on_disk.contains("changedBackedUp") && !on_disk.contains("patched"));
    // an install over unchanged files has nothing to report
    let (_, rep3) = install::install_report(&dirs, &inst, &b2, &InstallOptions::default(), &mut NoProgress).unwrap();
    assert!(rep3.changed_backed_up.is_empty(), "{rep3:?}");
}

#[test]
fn shroudforge_is_refused_unless_switching_and_never_chained() {
    let t = tmp("sf");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("Enshrouded");
    put(&game, "enshrouded.exe", b"MZ");
    let sf = build_test_dll(&["DirectInput8Create"], b"shroudforge/shroudforge-runtime.dll");
    put(&game, "dinput8.dll", &sf);
    let sf_winmm = build_test_dll(&["timeGetTime"], b"shroudforge helper");
    let sf_dbghelp = build_test_dll(&["MiniDumpWriteDump"], b"shroudforge/shroudforge-runtime.dll");
    put(&game, "winmm.dll", &sf_winmm);
    put(&game, "dbghelp.dll", &sf_dbghelp);
    put(&game, "shroudforge/shroudforge-runtime.dll", b"runtime");
    let before = outside_kiln(&game);
    let b = bundle::create(&stage(&t.0, "a", false), &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    let inst = client(&game);
    let e = install::install(&dirs, &inst, &b, &InstallOptions::default(), &mut NoProgress).unwrap_err();
    assert_eq!(e.code, ErrorCode::Refused);
    assert_eq!(outside_kiln(&game), before, "a refused install changes nothing");
    let r = install::install(&dirs, &inst, &b, &InstallOptions { switch_from_shroudforge: true, ..Default::default() }, &mut NoProgress).unwrap();
    // only its dinput8.dll moves: Kiln's proxy takes the name; its loader, helper and folder stay for Kiln's plugin
    assert_eq!(std::fs::read(game.join("winmm.dll")).unwrap(), sf_winmm);
    assert_eq!(std::fs::read(game.join("dbghelp.dll")).unwrap(), sf_dbghelp);
    assert!(game.join("shroudforge/shroudforge-runtime.dll").exists());
    assert!(chain::read(&game).unwrap().chain.is_empty(), "ShroudForge is never chained");
    assert_eq!(r.replaced.len(), 1);
    assert_eq!((r.replaced[0].path.as_str(), r.replaced[0].action, r.replaced[0].class),
               ("dinput8.dll", install::ReplacedAction::BackedUp, Some(install::ReplacedClass::ShroudForge)));
    install::uninstall(&dirs, &inst, &mut NoProgress).unwrap();
    assert_eq!(outside_kiln(&game), before);
}

#[test]
fn other_proxies_and_kiln_proxies() {
    let t = tmp("other");
    let dirs = DataDirs::at(t.0.join("data"));
    let b = bundle::create(&stage(&t.0, "a", false), &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    // an unknown proxy is chained as unknown-dinput8.dll
    let g1 = t.0.join("g1");
    put(&g1, "enshrouded.exe", b"MZ");
    put(&g1, "dinput8.dll", &build_test_dll(&["DirectInput8Create"], b"some dinput8 mod"));
    install::install(&dirs, &client(&g1), &b, &InstallOptions::default(), &mut NoProgress).unwrap();
    assert_eq!(chain::read(&g1).unwrap().chain[0].path, "chain\\unknown-dinput8.dll");
    // an older Kiln proxy (abi 1, deployed by hand) is replaced and backed up, not chained
    let g2 = t.0.join("g2");
    put(&g2, "enshrouded.exe", b"MZ");
    let old: Vec<u8> = build_test_dll(&["DirectInput8Create"], &"\\kiln\\kiln-core.dll".encode_utf16().flat_map(|c| c.to_le_bytes()).collect::<Vec<u8>>());
    put(&g2, "dinput8.dll", &old);
    let r = install::install(&dirs, &client(&g2), &b, &InstallOptions::default(), &mut NoProgress).unwrap();
    assert_eq!((r.replaced[0].class, r.replaced[0].action), (Some(install::ReplacedClass::Kiln), install::ReplacedAction::Replaced));
    assert!(chain::read(&g2).unwrap().chain.is_empty());
    install::uninstall(&dirs, &client(&g2), &mut NoProgress).unwrap();
    assert_eq!(std::fs::read(g2.join("dinput8.dll")).unwrap(), old);
    // not a DLL: refused
    let g3 = t.0.join("g3");
    put(&g3, "enshrouded.exe", b"MZ");
    put(&g3, "dinput8.dll", b"text file");
    assert_eq!(install::install(&dirs, &client(&g3), &b, &InstallOptions::default(), &mut NoProgress).unwrap_err().code, ErrorCode::Refused);
    // wrong exe for the side
    let g4 = t.0.join("g4");
    put(&g4, "enshrouded_server.exe", b"MZ");
    assert!(install::install(&dirs, &client(&g4), &b, &InstallOptions::default(), &mut NoProgress).is_err());
    // a server install takes the server sigdb
    let mut srv = client(&g4);
    srv.kind = GameKind::Server;
    srv.exe = "enshrouded_server.exe".into();
    srv.platform = Platform::Wine;
    let r = install::install(&dirs, &srv, &b, &InstallOptions::default(), &mut NoProgress).unwrap();
    assert!(r.files.iter().any(|f| f.path == "kiln/sigdb/server.json"));
    assert!(g4.join("kiln/sigdb/server.d").is_dir());
}

#[test]
fn refuses_while_steam_updates_the_game() {
    let t = tmp("updating");
    let dirs = DataDirs::at(t.0.join("data"));
    let lib = t.0.join("lib");
    let game = lib.join("steamapps/common/Enshrouded");
    put(&game, "enshrouded.exe", b"MZ");
    let acf = |flags: u32| format!(
        "\"AppState\"\n{{\n\t\"appid\"\t\t\"1203620\"\n\t\"name\"\t\t\"Enshrouded\"\n\t\"StateFlags\"\t\t\"{flags}\"\n\t\"installdir\"\t\t\"Enshrouded\"\n\t\"buildid\"\t\t\"26404966\"\n}}\n");
    put(&lib, "steamapps/appmanifest_1203620.acf", acf(1026).as_bytes());
    let b = bundle::create(&stage(&t.0, "a", false), &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    let mut inst = client(&game);
    inst.library = Some(lib.clone());
    let e = install::install(&dirs, &inst, &b, &InstallOptions::default(), &mut NoProgress).unwrap_err();
    assert_eq!(e.code, ErrorCode::SteamUpdating, "{e}");
    assert!(!game.join("kiln").exists());
    put(&lib, "steamapps/appmanifest_1203620.acf", acf(4).as_bytes());
    install::install(&dirs, &inst, &b, &InstallOptions::default(), &mut NoProgress).unwrap();
}

#[cfg(target_os = "linux")]
#[test]
fn refuses_while_the_game_runs() {
    let t = tmp("running");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("Enshrouded");
    put(&game, "enshrouded.exe", b"MZ");
    // a process named like the game with the game folder as its cwd (what Proton's game process looks like here)
    let fake = t.0.join("bin/enshrouded.exe");
    std::fs::create_dir_all(fake.parent().unwrap()).unwrap();
    std::fs::copy("/bin/sleep", &fake).unwrap();
    let mut child = std::process::Command::new(&fake).arg("30").current_dir(&game).spawn().unwrap();
    std::thread::sleep(std::time::Duration::from_millis(200));
    let b = bundle::create(&stage(&t.0, "a", false), &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    let r = install::install(&dirs, &client(&game), &b, &InstallOptions::default(), &mut NoProgress);
    child.kill().unwrap();
    let _ = child.wait();
    let e = r.unwrap_err();
    assert_eq!(e.code, ErrorCode::GameRunning, "{e}");
    assert_eq!(e.detail.unwrap()["pid"], child.id());
}

/// Every file below `dir` (relative path -> sha256), without kiln/backups/ (the installer's copies).
fn tree(dir: &Path) -> Vec<(String, String)> {
    fn walk(root: &Path, d: &Path, out: &mut Vec<(String, String)>) {
        for e in std::fs::read_dir(d).unwrap().flatten() {
            let p = e.path();
            let rel = p.strip_prefix(root).unwrap().to_string_lossy().replace('\\', "/");
            if rel == "kiln/backups" {
                continue;
            }
            if p.is_dir() {
                walk(root, &p, out);
            } else {
                out.push((rel, hash::sha256_file(&p).unwrap().0));
            }
        }
    }
    let mut v = Vec::new();
    walk(dir, dir, &mut v);
    v.sort();
    v
}

/// A client folder that already holds Kiln with the bundle's exact bytes (as client.sh / deploy-server.sh leave it).
fn predeployed(t: &Path, stage: &Path, game: &Path) {
    put(game, "enshrouded.exe", b"MZ");
    for comp in ["core", "sigdb-client"] {
        kiln_manage::util::copy_tree(&stage.join(comp), game).unwrap();
    }
    put(game, "kiln/mods/my-mod/kiln.mod.json", b"{\"id\": \"my-mod\"}"); // the user's, not the bundle's
    let _ = t;
}

#[test]
fn preexisting_identical_files_are_adopted_and_survive_uninstall() {
    let t = tmp("adopt");
    let dirs = DataDirs::at(t.0.join("data"));
    let s = stage(&t.0, "a", true);
    let game = t.0.join("Enshrouded");
    predeployed(&t.0, &s, &game);
    let before = tree(&game);
    let b = bundle::create(&s, &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    let inst = client(&game);
    let r = install::install(&dirs, &inst, &b, &InstallOptions::default(), &mut NoProgress).unwrap();
    // every pre-existing path is recorded: the Kiln proxy (backed up), every other identical file adopted
    let adopted: Vec<&str> = r.replaced.iter().filter(|x| x.action == install::ReplacedAction::Adopted).map(|x| x.path.as_str()).collect();
    assert!(adopted.contains(&"kiln/kiln-core.dll") && adopted.contains(&"kiln/kiln.json") && adopted.contains(&"kiln/sigdb/client.json"), "{adopted:?}");
    assert_eq!(adopted.len() + 1, r.files.len(), "every bundle file existed before");
    assert!(r.replaced.iter().any(|x| x.path == "dinput8.dll" && x.class == Some(install::ReplacedClass::Kiln)));
    assert!(r.replaced.iter().filter(|x| x.action == install::ReplacedAction::Adopted).all(|x| x.backup.is_none()));
    let rep = install::uninstall_report(&dirs, &inst, &mut NoProgress).unwrap();
    assert_eq!((rep.removed, rep.restored.as_slice()), (1, &["dinput8.dll".to_string()][..]), "only the proxy goes, and its original comes back");
    assert_eq!(rep.adopted.len(), adopted.len() - 1, "kiln.json is a config file and stays anyway");
    assert_eq!(tree(&game), before, "the folder is byte-identical to before");
}

#[test]
fn adopted_files_survive_an_upgrade() {
    let t = tmp("adopt-upgrade");
    let dirs = DataDirs::at(t.0.join("data"));
    let s1 = stage(&t.0, "a", true);
    let game = t.0.join("Enshrouded");
    predeployed(&t.0, &s1, &game);
    let before = tree(&game);
    let inst = client(&game);
    let b1 = bundle::create(&s1, &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    install::install(&dirs, &inst, &b1, &InstallOptions::default(), &mut NoProgress).unwrap();
    // b2: kiln-core.dll and dinput8.dll change, the two service DLLs are gone, the rest is the same
    let b2 = bundle::create(&stage(&t.0, "b", false), &t.0.join("out"), &opts(1_793_880_000, "bbbbbbb")).unwrap();
    let r2 = install::install(&dirs, &inst, &b2, &InstallOptions::default(), &mut NoProgress).unwrap();
    let rec = |p: &str| r2.replaced.iter().find(|x| x.path == p).cloned();
    assert_eq!(rec("kiln/host/Kiln.Host.dll").unwrap().action, install::ReplacedAction::Adopted, "unchanged: still adopted");
    let core = rec("kiln/kiln-core.dll").unwrap();
    assert_eq!(core.action, install::ReplacedAction::Replaced, "changed by the upgrade: its original is backed up");
    assert_eq!(std::fs::read(game.join(core.backup.unwrap())).unwrap(), b"core a");
    assert_eq!(std::fs::read(game.join("kiln/kiln-core.dll")).unwrap(), b"core b");
    // the bundle no longer has the service DLLs, but they were the user's before: they stay
    assert_eq!(std::fs::read(game.join("kiln/services/kiln-old.dll")).unwrap(), b"old service");
    assert_eq!(rec("kiln/services/kiln-old.dll").unwrap().action, install::ReplacedAction::Adopted);
    install::uninstall(&dirs, &inst, &mut NoProgress).unwrap();
    assert_eq!(tree(&game), before, "after upgrade + uninstall the folder is byte-identical to before the install");
    // a fresh folder still loses everything the installer brought
    let g2 = t.0.join("fresh");
    put(&g2, "enshrouded.exe", b"MZ");
    let fresh_before = tree(&g2);
    install::install(&dirs, &client(&g2), &b1, &InstallOptions::default(), &mut NoProgress).unwrap();
    install::install(&dirs, &client(&g2), &b2, &InstallOptions::default(), &mut NoProgress).unwrap();
    assert!(!g2.join("kiln/services/kiln-old.dll").exists(), "an obsolete file the installer brought goes on upgrade");
    install::uninstall(&dirs, &client(&g2), &mut NoProgress).unwrap();
    let left: Vec<(String, String)> = tree(&g2).into_iter().filter(|(p, _)| !p.starts_with("kiln/")).collect();
    assert_eq!(left, fresh_before);
    assert!(!g2.join("kiln/kiln-core.dll").exists() && g2.join("kiln/kiln.json").exists());
}

#[test]
fn builds_of_one_version_and_day_order_by_build_time_not_by_git_id() {
    let t = tmp("order");
    let dirs = DataDirs::at(t.0.join("data"));
    // the same day; the later build has the git id that sorts first as text
    let older = bundle::create(&stage(&t.0, "a", false), &t.0.join("out"), &opts(1_793_793_600, "f1ad110")).unwrap();
    let newer = bundle::create(&stage(&t.0, "b", false), &t.0.join("out"), &opts(1_793_793_600 + 3600, "8d20598")).unwrap();
    let a = bundle::import(&dirs, &older).unwrap();
    let b = bundle::import(&dirs, &newer).unwrap();
    let list = bundle::list(&dirs).unwrap();
    assert_eq!(list.iter().map(|x| x.version.as_str()).collect::<Vec<_>>(), [b.version.as_str(), a.version.as_str()], "newest first");
    let game = t.0.join("Enshrouded");
    put(&game, "enshrouded.exe", b"MZ");
    let inst = client(&game);
    install::install(&dirs, &inst, &older, &InstallOptions::default(), &mut NoProgress).unwrap();
    assert_eq!(install::status(&dirs, &inst).unwrap().upgrade_available.as_deref(), Some(b.version.as_str()));
    install::install(&dirs, &inst, &newer, &InstallOptions::default(), &mut NoProgress).unwrap();
    assert_eq!(install::status(&dirs, &inst).unwrap().upgrade_available, None, "the older build is no update");
}

#[test]
fn one_install_at_a_time_per_game_folder() {
    let t = tmp("lock");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("Enshrouded");
    put(&game, "enshrouded.exe", b"MZ");
    let b1 = bundle::create(&stage(&t.0, "a", false), &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    let inst = client(&game);
    let lock = game.join("kiln/.install.lock");
    // a live process holds it (this test's own): refused, nothing installed, the lock stays theirs
    put(&game, "kiln/.install.lock", format!("{} {}", std::process::id(), kiln_manage::util::now_unix()).as_bytes());
    let e = install::install(&dirs, &inst, &b1, &InstallOptions::default(), &mut NoProgress).unwrap_err();
    assert_eq!(e.code, ErrorCode::Conflict, "{e:?}");
    assert!(install::read_receipt(&game).unwrap().is_none() && lock.exists());
    // its process is gone: the lock is stale, taken over and removed at the end
    put(&game, "kiln/.install.lock", format!("{} {}", u32::MAX - 1, kiln_manage::util::now_unix()).as_bytes());
    install::install(&dirs, &inst, &b1, &InstallOptions::default(), &mut NoProgress).unwrap();
    assert!(!lock.exists(), "released after the install");
    install::uninstall(&dirs, &inst, &mut NoProgress).unwrap();
    assert!(!lock.exists(), "released after the uninstall");
}

/// "Switch from ShroudForge" with EML's dbghelp.dll and Shroudtopia's winmm.dll next to it: only ShroudForge's
/// dinput8.dll moves, the other loaders keep their names, uninstall restores everything byte-identically.
#[test]
fn switching_from_shroudforge_leaves_other_loaders_in_place() {
    let t = tmp("sf-others");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("Enshrouded");
    put(&game, "enshrouded.exe", b"MZ");
    put(&game, "dinput8.dll", &build_test_dll(&["DirectInput8Create", "ShroudforgeSetDinput8Provider"], b""));
    let eml = build_test_dll(&["MiniDumpWriteDump"], b"EML_LOG_FILE_PATH");
    let topia = build_test_dll(&["timeGetTime"], b"shroudtopia.dll");
    let set = build_test_dll(&["PowerGetActiveScheme"], b"shrouded_engine_tweaks\\plugins");
    put(&game, "dbghelp.dll", &eml);
    put(&game, "winmm.dll", &topia);
    put(&game, "powrprof.dll", &set);
    assert_eq!(kiln_manage::pe::classify_proxy(&game.join("dbghelp.dll")).unwrap().kind, kiln_manage::pe::ProxyKind::Eml);
    assert_eq!(kiln_manage::pe::classify_proxy(&game.join("winmm.dll")).unwrap().kind, kiln_manage::pe::ProxyKind::Shroudtopia);
    let slots = kiln_manage::engines::scan_proxies(&game);
    assert_eq!(slots.iter().map(|s| s.name.as_str()).collect::<Vec<_>>(), ["dinput8.dll", "dbghelp.dll", "winmm.dll", "powrprof.dll"]);
    assert_eq!(slots[3].class.kind, kiln_manage::pe::ProxyKind::Set);
    let before = outside_kiln(&game);
    let b = bundle::create(&stage(&t.0, "a", false), &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    let inst = client(&game);
    let r = install::install(&dirs, &inst, &b, &InstallOptions { switch_from_shroudforge: true, ..Default::default() }, &mut NoProgress).unwrap();
    assert_eq!(std::fs::read(game.join("dbghelp.dll")).unwrap(), eml, "EML's dbghelp.dll stays");
    assert_eq!(std::fs::read(game.join("winmm.dll")).unwrap(), topia, "Shroudtopia's winmm.dll stays");
    assert_eq!(std::fs::read(game.join("powrprof.dll")).unwrap(), set, "SET's powrprof.dll stays");
    assert_eq!(r.replaced.len(), 1);
    assert!(chain::read(&game).unwrap().chain.is_empty());
    install::uninstall(&dirs, &inst, &mut NoProgress).unwrap();
    assert_eq!(outside_kiln(&game), before);
}

/// EMF 0.4+ exports ShroudForge's chain-host function too: it is chained as EMF, never refused as ShroudForge.
#[test]
fn emf_0_5_is_chained_not_refused_as_shroudforge() {
    let t = tmp("emf05");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("Enshrouded");
    put(&game, "enshrouded.exe", b"MZ");
    let emf = build_test_dll(
        &["DirectInput8Create", "DllCanUnloadNow", "DllGetClassObject", "DllRegisterServer", "DllUnregisterServer", "GetdfDIJoystick",
          "ShroudforgeSetDinput8Provider", "emf_loader_identity", "emf_loader_play_once"],
        b"EMF-LOADER-7d3c9f\0shroudforge/shroudforge-runtime.dll\0",
    );
    put(&game, "dinput8.dll", &emf);
    put(&game, "dinput8-chain.ini", b"[EMF]\nshroudforge.dll\n");
    let before = outside_kiln(&game);
    let b = bundle::create(&stage(&t.0, "a", false), &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    let inst = client(&game);
    let r = install::install(&dirs, &inst, &b, &InstallOptions::default(), &mut NoProgress).unwrap();
    assert_eq!(chain::read(&game).unwrap().chain[0].path, "chain\\emf-dinput8.dll");
    assert_eq!((r.replaced[0].class, r.replaced[0].action), (Some(install::ReplacedClass::Emf), install::ReplacedAction::Chained));
    install::uninstall(&dirs, &inst, &mut NoProgress).unwrap();
    assert_eq!(outside_kiln(&game), before);
    assert_eq!(std::fs::read(game.join("dinput8.dll")).unwrap(), emf);
}

/// Other engines' dinput8.dll are chained under their own word; Ultimate ASI Loader under xinput9_1_0.dll.
#[test]
fn other_engines_are_chained_under_their_word() {
    let t = tmp("engines");
    let dirs = DataDirs::at(t.0.join("data"));
    let b = bundle::create(&stage(&t.0, "a", false), &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    for (tag, dll, line, engine) in [
        ("flame", build_test_dll(&["DirectInput8Create"], b"FlameStartRuntime\0flame.json"), "chain\\flame-dinput8.dll", kiln_manage::pe::ProxyKind::Flame),
        ("mlp", build_test_dll(&["DirectInput8Create"], b"modloader-proxy.dll"), "chain\\modloader-proxy-dinput8.dll", kiln_manage::pe::ProxyKind::ModLoaderProxy),
        ("asi", build_test_dll(&["DirectInput8Create", "IsUltimateASILoader"], b""), "..\\xinput9_1_0.dll", kiln_manage::pe::ProxyKind::UltimateAsiLoader),
    ] {
        let g = t.0.join(tag);
        put(&g, "enshrouded.exe", b"MZ");
        put(&g, "dinput8.dll", &dll);
        let before = outside_kiln(&g);
        let r = install::install(&dirs, &client(&g), &b, &InstallOptions::default(), &mut NoProgress).unwrap();
        assert_eq!(chain::read(&g).unwrap().chain[0].path, line, "{tag}");
        assert_eq!((r.replaced[0].action, r.replaced[0].engine), (install::ReplacedAction::Chained, Some(engine)), "{tag}");
        install::uninstall(&dirs, &client(&g), &mut NoProgress).unwrap();
        assert_eq!(outside_kiln(&g), before, "{tag}: uninstall restores the folder");
        assert!(chain::read(&g).unwrap().chain.is_empty());
    }
    // the name Ultimate ASI Loader needs is taken: refused, nothing changed
    let g = t.0.join("asi-taken");
    put(&g, "enshrouded.exe", b"MZ");
    put(&g, "dinput8.dll", &build_test_dll(&["DirectInput8Create", "IsUltimateASILoader"], b""));
    put(&g, "xinput9_1_0.dll", b"something else");
    let before = outside_kiln(&g);
    assert_eq!(install::install(&dirs, &client(&g), &b, &InstallOptions::default(), &mut NoProgress).unwrap_err().code, ErrorCode::Refused);
    assert_eq!(outside_kiln(&g), before);
}
