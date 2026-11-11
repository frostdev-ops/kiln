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
    "emf": { "sides": ["server", "client"], "default": true, "requires": ["core"] },
    "samples": { "sides": ["server"], "default": false, "requires": ["core"] }
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
    put(&s, "samples/kiln/mods/hello/Hello.dll", b"hello sample");
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
fn every_compatibility_component_is_default_on_both_sides() {
    use kiln_manage::manifest::RunSide;
    let t = tmp("compat-defaults");
    let s = stage(&t.0, "compat", false);
    let manifest = s.join("kiln-runtime.json");
    let mut m: serde_json::Value = serde_json::from_slice(&std::fs::read(&manifest).unwrap()).unwrap();
    for (component, file) in [
        ("eml", "kiln/plugins/kiln-eml.dll"),
        ("shroudforge", "kiln/plugins/shroudforge/shroudforge-runtime.dll"),
        ("enshwick", "kiln/plugins/enshwick/enshwick.dll"),
    ] {
        m["components"][component] = serde_json::json!({"sides": ["server", "client"], "default": true, "requires": ["core"]});
        put(&s, &format!("{component}/{file}"), b"compatibility runtime");
    }
    std::fs::write(manifest, serde_json::to_vec(&m).unwrap()).unwrap();
    let zip = bundle::create(&s, &t.0.join("out"), &opts(1_793_793_600, "0f1ffa6")).unwrap();
    let m = bundle::verify(&zip, &mut NoProgress).unwrap();
    for (side, sigdb) in [(RunSide::Client, "sigdb-client"), (RunSide::Server, "sigdb-server")] {
        assert_eq!(install::select_components(&m, side, None).unwrap(), ["core", "emf", "eml", "enshwick", "shroudforge", sigdb]);
        let plan = install::plan(&m, side, None).unwrap();
        for comp in ["emf", "eml", "enshwick", "shroudforge"] {
            assert!(plan.iter().any(|f| f.component == comp), "{comp} missing for {side:?}");
        }
        // Explicit selections remain respected; the installer adds core once and deduplicates the request.
        assert_eq!(install::select_components(&m, side, Some(&["enshwick".into(), "enshwick".into()])).unwrap(), ["core", "enshwick"]);
    }
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
    // client plan: core + sigdb-client + emf (default on both sides since 0.2.0-dev.18), no server sigdb
    let plan: Vec<String> = install::plan(&m, kiln_manage::manifest::RunSide::Client, None).unwrap().into_iter().map(|f| f.path).collect();
    assert!(plan.contains(&"kiln/sigdb/client.json".into()) && !plan.contains(&"kiln/sigdb/server.json".into()));
    assert!(plan.contains(&"kiln/plugins/kiln-emf.dll".into()));
    assert!(!plan.contains(&"kiln/mods/hello/Hello.dll".into()));
    // server plan: its defaults, no samples (not default)
    let plan: Vec<String> = install::plan(&m, kiln_manage::manifest::RunSide::Server, None).unwrap().into_iter().map(|f| f.path).collect();
    assert!(plan.contains(&"kiln/sigdb/server.json".into()) && plan.contains(&"kiln/plugins/kiln-emf.dll".into()));
    assert!(!plan.contains(&"kiln/mods/hello/Hello.dll".into()) && !plan.contains(&"kiln/sigdb/client.json".into()));
    assert_eq!(install::select_components(&m, kiln_manage::manifest::RunSide::Client, None).unwrap(), ["core", "emf", "sigdb-client"]);
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

/// Windows drops trailing dots and spaces: `".. "` is `..` there, `"a."` is `a`. Refuse them on every platform, so a
/// bundle or modpack that works on Linux cannot escape or collide on Windows.
#[test]
fn names_windows_would_normalise_are_refused() {
    for bad in ["a/.. /b", ".. ", "a/...", "...", "a/b.", "a/b ", "a/ b /c", "a/. /b", ". ", "a./b", "kiln/x.dll.", "kiln/x.dll "] {
        let e = bundle::safe_rel_path(bad).unwrap_err();
        assert_eq!(e.code, ErrorCode::Refused, "{bad:?}");
    }
    // dots and spaces inside or at the start of a name are fine on every platform
    for ok in ["a b/c d.txt", " lead/x", "a.b/c..d", ".hidden/x", "x/.config", "name with spaces.dll", "a/..b", "a/b..c"] {
        assert!(bundle::safe_rel_path(ok).is_ok(), "{ok:?}");
    }
    // the same guard on a manifest listing and on a zip entry name
    let m = |path: &str| -> kiln_manage::bundle::RuntimeManifest {
        serde_json::from_value(serde_json::json!({
            "format": 1, "version": "0.2.0-dev+20261104.0f1ffa6", "kiln": "0.2.0-dev", "abi": 2,
            "components": { "core": { "sides": ["client"] } },
            "files": [{ "path": path, "size": 1, "sha256": "ca978112ca1bbdcafac231b39a23dc4da786eff8147c4e72b9807785afee48bb", "component": "core" }]
        })).unwrap()
    };
    assert!(bundle::validate(&m("kiln/ok.dll")).is_ok());
    assert!(bundle::validate(&m("kiln/.. /evil.dll")).is_err());
    assert!(bundle::validate(&m("kiln/evil.dll.")).is_err());
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

/// Kiln's proxy changed since the install (a hand-deployed build: tools' client.sh, deploy-server.sh): uninstall moves it
/// aside and puts the chained original loader back, as before dev.24. Keeping it would leave a Kiln proxy without its
/// runtime in the game folder while the original loader is unchained and the receipt is gone.
#[test]
fn uninstall_sets_a_changed_kiln_proxy_aside_and_restores_the_chained_loader() {
    let t = tmp("changed-kiln-proxy");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("Enshrouded");
    put(&game, "enshrouded.exe", b"MZ fake game");
    let emf = build_test_dll(&["DirectInput8Create", "emf_loader_identity", "emf_loader_play_once"], b"EMF loader");
    put(&game, "dinput8.dll", &emf);
    let before = outside_kiln(&game);
    let b1 = bundle::create(&stage(&t.0, "a", false), &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    let inst = client(&game);
    install::install(&dirs, &inst, &b1, &InstallOptions::default(), &mut NoProgress).unwrap();
    let hand = kiln_proxy("0.2.0-dev+hand-build");
    put(&game, "dinput8.dll", &hand);
    let report = install::uninstall_report(&dirs, &inst, &mut NoProgress).unwrap();
    assert_eq!(std::fs::read(game.join("dinput8.dll")).unwrap(), emf, "the chained loader is back: {report:?}");
    assert_eq!(outside_kiln(&game), before);
    let aside: Vec<PathBuf> = std::fs::read_dir(game.join("kiln/backups")).unwrap().flatten().map(|e| e.path().join("dinput8.dll"))
        .filter(|p| p.parent().unwrap().file_name().unwrap().to_string_lossy().starts_with("uninstall-") && p.is_file()).collect();
    assert_eq!(aside.len(), 1, "{report:?}");
    assert_eq!(std::fs::read(&aside[0]).unwrap(), hand, "the changed build is kept");
    assert!(install::read_receipt(&game).unwrap().is_none());
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
    for comp in ["core", "sigdb-client", "emf"] { // the client defaults
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

#[test]
fn chain_destinations_never_displace_even_identical_files() {
    let t = tmp("occupied");
    let dirs = DataDirs::at(t.0.join("data"));
    let b = bundle::create(&stage(&t.0, "a", false), &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    let dll = build_test_dll(&["DirectInput8Create"], b"EML_LOG_FILE_PATH");
    let g = t.0.join("chain");
    put(&g, "enshrouded.exe", b"MZ");
    put(&g, "dinput8.dll", &dll);
    put(&g, "kiln/chain/eml-dinput8.dll", &dll);
    std::fs::create_dir_all(g.join("kiln/chain/eml-dinput8-2.dll")).unwrap();
    let r = install::install(&dirs, &client(&g), &b, &InstallOptions::default(), &mut NoProgress).unwrap();
    assert_eq!(r.replaced[0].chain_path.as_deref(), Some("kiln/chain/eml-dinput8-3.dll"));
    install::uninstall(&dirs, &client(&g), &mut NoProgress).unwrap();
    assert_eq!(std::fs::read(g.join("kiln/chain/eml-dinput8.dll")).unwrap(), dll);
    assert_eq!(std::fs::read(g.join("dinput8.dll")).unwrap(), dll);
    let asi = build_test_dll(&["DirectInput8Create", "IsUltimateASILoader"], b"");
    for tag in ["same", "directory", "case"] {
        let g = t.0.join(tag);
        put(&g, "enshrouded.exe", b"MZ");
        put(&g, "dinput8.dll", &asi);
        match tag {
            "same" => put(&g, "xinput9_1_0.dll", &asi),
            "case" => put(&g, "XINPUT9_1_0.DLL", &asi),
            _ => std::fs::create_dir_all(g.join("xinput9_1_0.dll")).unwrap(),
        }
        assert_eq!(install::install(&dirs, &client(&g), &b, &InstallOptions::default(), &mut NoProgress).unwrap_err().code, ErrorCode::Refused);
        assert_eq!(std::fs::read(g.join("dinput8.dll")).unwrap(), asi);
    }
}

#[test]
fn install_errors_restore_proxy_and_chain_ini_including_receipt_failure() {
    use kiln_manage::task::Progress;
    struct FailReceipt { path: PathBuf }
    impl Progress for FailReceipt {
        fn report(&mut self, _: Option<f32>, message: &str) {
            if message == "installed dinput8.dll" {
                std::fs::create_dir_all(&self.path).unwrap();
            }
        }
    }
    let t = tmp("rollback");
    let dirs = DataDirs::at(t.0.join("data"));
    let b = bundle::create(&stage(&t.0, "a", false), &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    for (tag, dll, sf) in [
        ("eml", build_test_dll(&["DirectInput8Create"], b"EML_LOG_FILE_PATH"), false),
        ("sf", build_test_dll(&["ShroudforgeSetDinput8Provider"], b""), true),
        ("kiln", kiln_proxy("old"), false),
        ("wine", build_test_dll(&[], b""), false),
    ] {
        let g = t.0.join(tag);
        let mut dll = dll;
        if tag == "wine" { dll[0x40..0x50].copy_from_slice(b"Wine builtin DLL"); }
        put(&g, "enshrouded.exe", b"MZ");
        put(&g, "dinput8.dll", &dll);
        let original_ini = b"; user formatting\n[chain]\n  user.dll\n[Other]\nx=1\n";
        put(&g, "kiln/chain.ini", original_ini);
        put(&g, "kiln/kiln-core.dll", b"old core");
        let mut fail = FailReceipt { path: g.join("kiln/.install.json") };
        assert!(install::install(&dirs, &client(&g), &b, &InstallOptions { switch_from_shroudforge: sf, ..Default::default() }, &mut fail).is_err());
        assert_eq!(std::fs::read(g.join("dinput8.dll")).unwrap(), dll, "{tag}");
        assert_eq!(std::fs::read(g.join("kiln/chain.ini")).unwrap(), original_ini, "{tag}");
        assert_eq!(std::fs::read(g.join("kiln/kiln-core.dll")).unwrap(), b"old core", "{tag}");
    }
}

#[test]
fn successive_installs_keep_independent_original_backups() {
    let t = tmp("backup-id");
    let dirs = DataDirs::at(t.0.join("data"));
    let b = bundle::create(&stage(&t.0, "a", false), &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    let g = t.0.join("game");
    let original = kiln_proxy("0.1.0");
    put(&g, "enshrouded.exe", b"MZ");
    put(&g, "dinput8.dll", &original);
    install::install(&dirs, &client(&g), &b, &InstallOptions::default(), &mut NoProgress).unwrap();
    put(&g, "dinput8.dll", &build_test_dll(&["emf_loader_identity"], b""));
    let r = install::install(&dirs, &client(&g), &b, &InstallOptions::default(), &mut NoProgress).unwrap();
    assert_ne!(r.replaced[0].backup, r.replaced[1].backup);
    assert_eq!(std::fs::read(g.join(r.replaced[0].backup.as_ref().unwrap())).unwrap(), original);
    install::uninstall(&dirs, &client(&g), &mut NoProgress).unwrap();
    assert_eq!(std::fs::read(g.join("dinput8.dll")).unwrap(), original);
}

#[test]
fn full_chain_lists_refuse_without_displacing_the_loader() {
    let t = tmp("chain-limit");
    let dirs = DataDirs::at(t.0.join("data"));
    let b = bundle::create(&stage(&t.0, "a", false), &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    for (tag, ini) in [("count", "a.dll\n".repeat(256)), ("size", ";".repeat(256 * 1024))] {
        let g = t.0.join(tag);
        let dll = build_test_dll(&["emf_loader_identity"], b"");
        put(&g, "enshrouded.exe", b"MZ");
        put(&g, "dinput8.dll", &dll);
        put(&g, "kiln/chain.ini", ini.as_bytes());
        assert_eq!(install::install(&dirs, &client(&g), &b, &InstallOptions::default(), &mut NoProgress).unwrap_err().code, ErrorCode::Refused);
        assert_eq!(std::fs::read(g.join("dinput8.dll")).unwrap(), dll);
        assert_eq!(std::fs::read(g.join("kiln/chain.ini")).unwrap(), ini.as_bytes());
    }
}

#[cfg(unix)]
#[test]
fn symlinks_and_dangling_chain_slots_never_write_outside_the_game() {
    use std::os::unix::fs::symlink;
    let t = tmp("symlinks");
    let dirs = DataDirs::at(t.0.join("data"));
    let b = bundle::create(&stage(&t.0, "a", false), &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    let dll = build_test_dll(&["emf_loader_identity"], b"");
    let outside = t.0.join("outside");
    std::fs::create_dir_all(&outside).unwrap();
    for rel in ["kiln", "kiln/chain", "dinput8.dll"] {
        let g = t.0.join(rel.replace('/', "-"));
        put(&g, "enshrouded.exe", b"MZ");
        if rel != "dinput8.dll" { put(&g, "dinput8.dll", &dll); }
        let link = g.join(rel);
        std::fs::create_dir_all(link.parent().unwrap()).unwrap();
        symlink(&outside, &link).unwrap();
        assert_eq!(install::install(&dirs, &client(&g), &b, &InstallOptions::default(), &mut NoProgress).unwrap_err().code, ErrorCode::Refused);
        assert_eq!(std::fs::read_dir(&outside).unwrap().count(), 0);
    }
    let g = t.0.join("dangling");
    put(&g, "enshrouded.exe", b"MZ");
    put(&g, "dinput8.dll", &dll);
    std::fs::create_dir_all(g.join("kiln/chain")).unwrap();
    symlink(outside.join("missing.dll"), g.join("kiln/chain/emf-dinput8.dll")).unwrap();
    let r = install::install(&dirs, &client(&g), &b, &InstallOptions::default(), &mut NoProgress).unwrap();
    assert_eq!(r.replaced[0].chain_path.as_deref(), Some("kiln/chain/emf-dinput8-2.dll"));
    install::uninstall(&dirs, &client(&g), &mut NoProgress).unwrap();
    assert!(g.join("kiln/chain/emf-dinput8.dll").is_symlink());
}

#[test]
fn runtime_files_cannot_claim_other_proxy_or_recovery_paths() {
    let t = tmp("reserved");
    let dirs = DataDirs::at(t.0.join("data"));
    let dll = build_test_dll(&["emf_loader_identity"], b"");
    for (i, rel) in ["winmm.dll", "kiln/chain/emf-dinput8.dll", "kiln/backups/original.dll", "kiln/.install.json", "kiln/chain.ini"].iter().enumerate() {
        let s = stage(&t.0, &format!("a{i}"), false);
        put(&s, &format!("core/{rel}"), b"bundle file");
        let b = bundle::create(&s, &t.0.join(format!("out{i}")), &opts(1_793_793_600, "aaaaaaa")).unwrap();
        let g = t.0.join(format!("game{i}"));
        put(&g, "enshrouded.exe", b"MZ");
        put(&g, "dinput8.dll", &dll);
        put(&g, "winmm.dll", b"another loader");
        assert_eq!(install::install(&dirs, &client(&g), &b, &InstallOptions::default(), &mut NoProgress).unwrap_err().code, ErrorCode::Refused);
        assert_eq!(std::fs::read(g.join("dinput8.dll")).unwrap(), dll);
        assert_eq!(std::fs::read(g.join("winmm.dll")).unwrap(), b"another loader");
    }
}

#[test]
fn failed_undo_keeps_originals_in_recovery_staging_on_later_attempts() {
    use kiln_manage::task::Progress;
    struct BlockUndo(PathBuf);
    impl Progress for BlockUndo {
        fn report(&mut self, _: Option<f32>, message: &str) {
            if message == "installed dinput8.dll" {
                std::fs::remove_file(self.0.join("dinput8.dll")).unwrap();
                std::fs::create_dir(self.0.join("dinput8.dll")).unwrap();
                std::fs::create_dir(self.0.join("kiln/.install.json")).unwrap();
            }
        }
    }
    let t = tmp("failed-undo");
    let dirs = DataDirs::at(t.0.join("data"));
    let b = bundle::create(&stage(&t.0, "a", false), &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    let g = t.0.join("game");
    let original = build_test_dll(&["ShroudforgeSetDinput8Provider"], b"");
    put(&g, "enshrouded.exe", b"MZ");
    put(&g, "dinput8.dll", &original);
    let options = InstallOptions { switch_from_shroudforge: true, ..Default::default() };
    let err = install::install(&dirs, &client(&g), &b, &options, &mut BlockUndo(g.clone())).unwrap_err();
    assert!(err.message.contains("rollback incomplete"), "{err}");
    let recovery = std::fs::read_dir(g.join("kiln")).unwrap().flatten().find(|e| e.file_name().to_string_lossy().starts_with(".staging-")).unwrap().path();
    assert_eq!(std::fs::read(recovery.join("previous/dinput8.dll")).unwrap(), original);
    std::fs::remove_dir(g.join("kiln/.install.json")).unwrap();
    assert!(install::install(&dirs, &client(&g), &b, &options, &mut NoProgress).is_err());
    assert_eq!(std::fs::read(recovery.join("previous/dinput8.dll")).unwrap(), original);
}

#[test]
fn receipt_paths_cannot_escape_the_game_folder() {
    let t = tmp("receipt-path");
    let dirs = DataDirs::at(t.0.join("data"));
    let b = bundle::create(&stage(&t.0, "a", false), &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    let g = t.0.join("game");
    put(&g, "enshrouded.exe", b"MZ");
    let r = install::install(&dirs, &client(&g), &b, &InstallOptions::default(), &mut NoProgress).unwrap();
    let mut tampered = r.clone();
    tampered.files[0].path = "../outside.dll".into();
    put(&t.0, "outside.dll", b"keep");
    kiln_manage::util::write_json_atomic(&g.join("kiln/.install.json"), &tampered).unwrap();
    assert!(install::uninstall(&dirs, &client(&g), &mut NoProgress).is_err());
    assert_eq!(std::fs::read(t.0.join("outside.dll")).unwrap(), b"keep");
    kiln_manage::util::write_json_atomic(&g.join("kiln/.install.json"), &r).unwrap();
    install::uninstall(&dirs, &client(&g), &mut NoProgress).unwrap();
}

#[cfg(unix)]
#[test]
fn uninstall_refuses_symlinked_paths_before_removing_any_files() {
    let t = tmp("uninstall-link");
    let dirs = DataDirs::at(t.0.join("data"));
    let b = bundle::create(&stage(&t.0, "a", false), &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    let g = t.0.join("game");
    let original = build_test_dll(&["emf_loader_identity"], b"");
    put(&g, "enshrouded.exe", b"MZ");
    put(&g, "dinput8.dll", &original);
    let r = install::install(&dirs, &client(&g), &b, &InstallOptions::default(), &mut NoProgress).unwrap();
    let outside = t.0.join("outside");
    std::fs::rename(g.join("kiln/host"), &outside).unwrap();
    std::os::unix::fs::symlink(&outside, g.join("kiln/host")).unwrap();
    assert_eq!(install::uninstall(&dirs, &client(&g), &mut NoProgress).unwrap_err().code, ErrorCode::Refused);
    assert_eq!(std::fs::read(outside.join("Kiln.Host.dll")).unwrap(), b"host");
    assert_eq!(install::read_receipt(&g).unwrap(), Some(r));
    std::fs::remove_file(g.join("kiln/host")).unwrap();
    std::fs::rename(&outside, g.join("kiln/host")).unwrap();
    install::uninstall(&dirs, &client(&g), &mut NoProgress).unwrap();
    assert_eq!(std::fs::read(g.join("dinput8.dll")).unwrap(), original);
}

/// `stage`, plus components: (name, default). Each gets one file `kiln/plugins/<name>/<name>.dll`.
fn stage_plus(root: &Path, version_tag: &str, extra: &[(&str, bool)]) -> PathBuf {
    let s = stage(root, version_tag, false);
    let manifest = s.join("kiln-runtime.json");
    let mut m: serde_json::Value = serde_json::from_slice(&std::fs::read(&manifest).unwrap()).unwrap();
    for (name, default) in extra {
        m["components"][*name] = serde_json::json!({"sides": ["server", "client"], "default": default, "requires": ["core"]});
        put(&s, &format!("{name}/kiln/plugins/{name}/{name}.dll"), format!("{name} {version_tag}").as_bytes());
    }
    std::fs::write(manifest, serde_json::to_vec(&m).unwrap()).unwrap();
    s
}

/// The compatibility components dev.18 made default on both sides.
const COMPAT: [(&str, bool); 3] = [("eml", true), ("shroudforge", true), ("enshwick", true)];

/// A receipt as kiln-manage wrote it before `runtime.deselected` existed: a plain component list.
fn make_legacy(game: &Path, components: &[&str]) {
    let p = game.join("kiln/.install.json");
    let mut v: serde_json::Value = serde_json::from_slice(&std::fs::read(&p).unwrap()).unwrap();
    v["runtime"]["components"] = serde_json::json!(components);
    v["runtime"].as_object_mut().unwrap().remove("deselected");
    std::fs::write(&p, serde_json::to_vec_pretty(&v).unwrap()).unwrap();
}

fn comps(r: &install::InstallReceipt) -> Vec<&str> {
    r.runtime.components.iter().map(String::as_str).collect()
}

#[test]
fn a_fresh_install_gets_the_defaults_and_turns_nothing_off() {
    let t = tmp("parts-fresh");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("Enshrouded");
    put(&game, "enshrouded.exe", b"MZ fake game");
    let b = bundle::create(&stage_plus(&t.0, "a", &[("eml", true), ("extra", false)]), &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    let r = install::install(&dirs, &client(&game), &b, &InstallOptions::default(), &mut NoProgress).unwrap();
    assert_eq!(comps(&r), ["core", "emf", "eml", "sigdb-client"]);
    assert_eq!(r.runtime.deselected.as_deref(), Some(&[][..]), "a new receipt says what was turned off, even when nothing was");
    assert!(game.join("kiln/plugins/eml/eml.dll").is_file() && !game.join("kiln/plugins/extra").exists());
    let on_disk = std::fs::read_to_string(game.join("kiln/.install.json")).unwrap();
    assert!(on_disk.contains("\"deselected\": []"), "{on_disk}");
}

#[test]
fn an_old_receipt_with_a_custom_list_gets_new_and_newly_default_components_on_update() {
    let t = tmp("parts-legacy");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("Enshrouded");
    put(&game, "enshrouded.exe", b"MZ fake game");
    let inst = client(&game);
    // the old bundle: `late` exists but is optional; the player's list (they once added emf by hand) is all it records
    let b1 = bundle::create(&stage_plus(&t.0, "a", &[("late", false)]), &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    install::install(&dirs, &inst, &b1, &InstallOptions::default(), &mut NoProgress).unwrap();
    make_legacy(&game, &["core", "emf", "sigdb-client"]);
    // the new bundle: eml, shroudforge, enshwick are new defaults, `late` became one, `extra` is new but optional
    let b2 = bundle::create(&stage_plus(&t.0, "b", &[COMPAT[0], COMPAT[1], COMPAT[2], ("late", true), ("extra", false)]), &t.0.join("out"),
        &opts(1_793_880_000, "bbbbbbb")).unwrap();
    let r = install::install(&dirs, &inst, &b2, &InstallOptions::default(), &mut NoProgress).unwrap();
    assert_eq!(comps(&r), ["core", "emf", "eml", "enshwick", "late", "shroudforge", "sigdb-client"]);
    assert_eq!(r.runtime.deselected.as_deref(), Some(&[][..]));
    for c in ["eml", "shroudforge", "enshwick", "late"] {
        assert!(game.join(format!("kiln/plugins/{c}/{c}.dll")).is_file(), "{c} is installed");
    }
    assert!(!game.join("kiln/plugins/extra").exists(), "a new optional component is not installed");
    assert!(install::status(&dirs, &inst).unwrap().modified.is_empty());
}

#[test]
fn a_legacy_receipt_already_on_the_new_bundle_shows_and_gets_its_missing_parts() {
    // the reported client: the update to dev.18 kept the old list, so the receipt names dev.18 without its new parts
    let t = tmp("parts-missing");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("Enshrouded");
    put(&game, "enshrouded.exe", b"MZ fake game");
    let inst = client(&game);
    let b = bundle::create(&stage_plus(&t.0, "b", &COMPAT), &t.0.join("out"), &opts(1_793_880_000, "bbbbbbb")).unwrap();
    let b = bundle::import(&dirs, &b).unwrap().file;
    install::install(&dirs, &inst, &b, &InstallOptions { components: Some(vec!["core".into(), "emf".into(), "sigdb-client".into()]), ..Default::default() },
        &mut NoProgress).unwrap();
    make_legacy(&game, &["core", "emf", "sigdb-client"]);
    let st = install::status(&dirs, &inst).unwrap();
    assert_eq!(st.missing_parts, ["eml", "enshwick", "shroudforge"]);
    assert_eq!(st.upgrade_available, None);
    // installing the same bundle again (Runtime's Reinstall, `kilnctl client install`) adds them
    let r = install::install(&dirs, &inst, &b, &InstallOptions::default(), &mut NoProgress).unwrap();
    assert_eq!(comps(&r), ["core", "emf", "eml", "enshwick", "shroudforge", "sigdb-client"]);
    assert!(game.join("kiln/plugins/shroudforge/shroudforge.dll").is_file());
    assert!(install::status(&dirs, &inst).unwrap().missing_parts.is_empty());
}

#[test]
fn an_explicit_deselection_is_kept_across_updates() {
    let t = tmp("parts-off");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("Enshrouded");
    put(&game, "enshrouded.exe", b"MZ fake game");
    let inst = client(&game);
    let b1 = bundle::create(&stage_plus(&t.0, "a", &COMPAT), &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    let b1 = bundle::import(&dirs, &b1).unwrap().file;
    // the player turns eml and emf off
    let r1 = install::install(&dirs, &inst, &b1, &InstallOptions {
        components: Some(vec!["core".into(), "sigdb-client".into(), "shroudforge".into(), "enshwick".into()]),
        ..Default::default()
    }, &mut NoProgress).unwrap();
    assert_eq!(r1.runtime.deselected.as_deref(), Some(&["emf".to_string(), "eml".to_string()][..]));
    assert!(install::status(&dirs, &inst).unwrap().missing_parts.is_empty(), "turned off is not missing");
    // the next bundle has a new default; the update adds it and keeps the two off
    let b2 = bundle::create(&stage_plus(&t.0, "b", &[COMPAT[0], COMPAT[1], COMPAT[2], ("fresh", true)]), &t.0.join("out"),
        &opts(1_793_880_000, "bbbbbbb")).unwrap();
    let r2 = install::install(&dirs, &inst, &b2, &InstallOptions::default(), &mut NoProgress).unwrap();
    assert_eq!(comps(&r2), ["core", "enshwick", "fresh", "shroudforge", "sigdb-client"]);
    assert_eq!(r2.runtime.deselected, r1.runtime.deselected);
    assert!(!game.join("kiln/plugins/eml/eml.dll").exists() && !game.join("kiln/plugins/kiln-emf.dll").exists());
    // turning one back on is an explicit choice too: only emf stays off
    let r3 = install::install(&dirs, &inst, &b2, &InstallOptions {
        components: Some(vec!["core".into(), "sigdb-client".into(), "eml".into(), "shroudforge".into(), "enshwick".into(), "fresh".into()]),
        ..Default::default()
    }, &mut NoProgress).unwrap();
    assert_eq!(r3.runtime.deselected.as_deref(), Some(&["emf".to_string()][..]));
    assert!(game.join("kiln/plugins/eml/eml.dll").is_file());
}

#[test]
fn a_rollback_to_a_bundle_without_new_components_works_and_keeps_the_choice() {
    let t = tmp("parts-rollback");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("Enshrouded");
    put(&game, "enshrouded.exe", b"MZ fake game");
    let inst = client(&game);
    let old = bundle::create(&stage(&t.0, "a", false), &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    let new = bundle::create(&stage_plus(&t.0, "b", &COMPAT), &t.0.join("out"), &opts(1_793_880_000, "bbbbbbb")).unwrap();
    bundle::import(&dirs, &old).unwrap();
    let new = bundle::import(&dirs, &new).unwrap().file;
    // on the new bundle, shroudforge turned off
    install::install(&dirs, &inst, &new, &InstallOptions {
        components: Some(vec!["core".into(), "sigdb-client".into(), "emf".into(), "eml".into(), "enshwick".into()]),
        ..Default::default()
    }, &mut NoProgress).unwrap();
    // the old bundle has neither eml nor enshwick: they are left out, not an error
    let rb = install::rollback(&dirs, &inst, "0.2.0-dev+20261104.aaaaaaa", &mut NoProgress).unwrap();
    assert_eq!(comps(&rb), ["core", "emf", "sigdb-client"]);
    assert_eq!(rb.runtime.deselected.as_deref(), Some(&["shroudforge".to_string()][..]), "the opt-out survives the rollback");
    assert!(!game.join("kiln/plugins/eml/eml.dll").exists() && !game.join("kiln/plugins/enshwick/enshwick.dll").exists());
    assert!(install::status(&dirs, &inst).unwrap().modified.is_empty());
    // forward again: eml and enshwick come back, shroudforge stays off
    let up = install::install(&dirs, &inst, &new, &InstallOptions::default(), &mut NoProgress).unwrap();
    assert_eq!(comps(&up), ["core", "emf", "eml", "enshwick", "sigdb-client"]);
    // a legacy receipt on the new bundle also rolls back (it used to fail: "the bundle has no component eml")
    make_legacy(&game, &["core", "emf", "eml", "enshwick", "shroudforge", "sigdb-client"]);
    let rb2 = install::rollback(&dirs, &inst, "0.2.0-dev+20261104.aaaaaaa", &mut NoProgress).unwrap();
    assert_eq!(comps(&rb2), ["core", "emf", "sigdb-client"]);
    assert_eq!(rb2.runtime.deselected.as_deref(), Some(&[][..]));
}

#[test]
fn resolve_components_follows_the_side_and_explicit_choices() {
    use kiln_manage::manifest::RunSide;
    let t = tmp("parts-resolve");
    let b = bundle::create(&stage_plus(&t.0, "a", &COMPAT), &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    let m = bundle::read(&b).unwrap();
    let server = install::resolve_components(&m, RunSide::Server, None, None).unwrap();
    assert_eq!(server.components, ["core", "emf", "eml", "enshwick", "shroudforge", "sigdb-server"]);
    assert!(server.deselected.is_empty());
    // an explicit choice records the defaults it leaves out (not the optional samples)
    let pick = install::resolve_components(&m, RunSide::Server, None, Some(&["sigdb-server".into(), "samples".into()])).unwrap();
    assert_eq!(pick.components, ["core", "samples", "sigdb-server"]);
    assert_eq!(pick.deselected, ["emf", "eml", "enshwick", "shroudforge"]);
    // a receipt of the other side does not count
    let game = t.0.join("Enshrouded");
    put(&game, "enshrouded.exe", b"MZ fake game");
    let r = install::install(&DataDirs::at(t.0.join("data")), &client(&game), &b, &InstallOptions {
        components: Some(vec!["core".into(), "sigdb-client".into()]),
        ..Default::default()
    }, &mut NoProgress).unwrap();
    assert_eq!(install::resolve_components(&m, RunSide::Server, Some(&r), None).unwrap(), server);
    let again = install::resolve_components(&m, RunSide::Client, Some(&r), None).unwrap();
    assert_eq!(again.components, ["core", "sigdb-client"]);
}

#[test]
fn review_opt_out_survives_an_explicit_choice_while_the_part_is_optional_or_on_another_side() {
    use kiln_manage::manifest::RunSide;
    let t = tmp("parts-off-optional");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("game");
    put(&game, "enshrouded.exe", b"MZ");
    let b = bundle::create(&stage_plus(&t.0, "a", &[("eml", true)]), &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    let r = install::install(&dirs, &client(&game), &b, &InstallOptions {
        components: Some(vec!["core".into(), "sigdb-client".into()]), ..Default::default()
    }, &mut NoProgress).unwrap();
    let mut m = bundle::read(&b).unwrap();
    for sides in [vec![RunSide::Client], vec![RunSide::Server]] {
        m.components.get_mut("eml").unwrap().default = false;
        m.components.get_mut("eml").unwrap().sides = sides;
        let choice = install::resolve_components(&m, RunSide::Client, Some(&r), Some(&r.runtime.components)).unwrap();
        assert_eq!(choice.deselected, ["emf", "eml"]);
        let mut older = r.clone();
        older.runtime.deselected = Some(choice.deselected);
        m.components.get_mut("eml").unwrap().default = true;
        m.components.get_mut("eml").unwrap().sides = vec![RunSide::Client];
        assert!(!install::resolve_components(&m, RunSide::Client, Some(&older), None).unwrap().components.contains(&"eml".into()));
    }
}

#[test]
fn review_automatic_dependency_does_not_erase_an_opt_out() {
    use kiln_manage::manifest::RunSide;
    let t = tmp("parts-off-required");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("game");
    put(&game, "enshrouded.exe", b"MZ");
    let b = bundle::create(&stage_plus(&t.0, "a", &[("eml", true), ("bridge", false)]), &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    let r = install::install(&dirs, &client(&game), &b, &InstallOptions {
        components: Some(vec!["core".into(), "sigdb-client".into()]), ..Default::default()
    }, &mut NoProgress).unwrap();
    let mut m = bundle::read(&b).unwrap();
    m.components.get_mut("bridge").unwrap().default = true;
    m.components.get_mut("bridge").unwrap().requires = vec!["eml".into()];
    let required = install::resolve_components(&m, RunSide::Client, Some(&r), None).unwrap();
    assert!(required.components.contains(&"eml".into()), "requires takes precedence for this install");
    assert_eq!(required.deselected, ["emf", "eml"], "an automatic dependency is not a player changing their choice");
    let mut updated = r.clone();
    updated.runtime.components = required.components;
    updated.runtime.deselected = Some(required.deselected);
    m.components.remove("bridge");
    let later = install::resolve_components(&m, RunSide::Client, Some(&updated), None).unwrap();
    assert_eq!(later.components, ["core", "sigdb-client"]);
    assert_eq!(later.deselected, ["emf", "eml"]);
}

#[test]
fn review_status_includes_missing_transitive_requirements() {
    let t = tmp("parts-missing-required");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("game");
    put(&game, "enshrouded.exe", b"MZ");
    let s = stage_plus(&t.0, "a", &[("eml", true), ("helper", false), ("leaf", false)]);
    let path = s.join("kiln-runtime.json");
    let mut m: serde_json::Value = serde_json::from_slice(&std::fs::read(&path).unwrap()).unwrap();
    m["components"]["eml"]["requires"] = serde_json::json!(["helper"]);
    m["components"]["helper"]["requires"] = serde_json::json!(["leaf"]);
    std::fs::write(path, serde_json::to_vec(&m).unwrap()).unwrap();
    let b = bundle::create(&s, &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    let b = bundle::import(&dirs, &b).unwrap();
    install::install(&dirs, &client(&game), &b.file, &InstallOptions::default(), &mut NoProgress).unwrap();
    make_legacy(&game, &["core", "emf", "sigdb-client"]);
    assert_eq!(install::status(&dirs, &client(&game)).unwrap().missing_parts, ["eml", "helper", "leaf"]);
    let r = install::install(&dirs, &client(&game), &b.file, &InstallOptions::default(), &mut NoProgress).unwrap();
    assert_eq!(comps(&r), ["core", "emf", "eml", "helper", "leaf", "sigdb-client"]);
    assert!(install::status(&dirs, &client(&game)).unwrap().missing_parts.is_empty());
}

#[test]
fn review_deselection_removes_only_unchanged_files_and_receipt_failure_restores_the_old_choice() {
    use kiln_manage::task::Progress;
    struct FailReceipt(PathBuf);
    impl Progress for FailReceipt {
        fn report(&mut self, _: Option<f32>, message: &str) {
            if message == "installed dinput8.dll" {
                std::fs::create_dir_all(self.0.join("kiln/.install.json.tmp")).unwrap();
            }
        }
    }
    let t = tmp("parts-remove-atomic");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("game");
    put(&game, "enshrouded.exe", b"MZ");
    let s = stage_plus(&t.0, "a", &[("eml", true), ("fresh", false)]);
    put(&s, "eml/kiln/plugins/eml/edited.dll", b"original edit");
    put(&s, "eml/kiln/plugins/eml/settings.json", b"settings");
    let manifest_path = s.join("kiln-runtime.json");
    let mut manifest: serde_json::Value = serde_json::from_slice(&std::fs::read(&manifest_path).unwrap()).unwrap();
    manifest["config"] = serde_json::json!(["kiln/kiln.json", "kiln/plugins/eml/settings.json"]);
    std::fs::write(manifest_path, serde_json::to_vec(&manifest).unwrap()).unwrap();
    let b1 = bundle::create(&s, &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    let old = install::install(&dirs, &client(&game), &b1, &InstallOptions::default(), &mut NoProgress).unwrap();
    put(&game, "kiln/plugins/eml/edited.dll", b"player edit");
    let receipt_before = std::fs::read(game.join("kiln/.install.json")).unwrap();
    let b2 = bundle::create(&stage_plus(&t.0, "b", &[("eml", true), ("fresh", false)]), &t.0.join("out"), &opts(1_793_880_000, "bbbbbbb")).unwrap();
    let options = InstallOptions { components: Some(vec!["core".into(), "sigdb-client".into(), "fresh".into()]), ..Default::default() };
    let err = install::install(&dirs, &client(&game), &b2, &options, &mut FailReceipt(game.clone())).unwrap_err();
    assert_eq!(err.code, ErrorCode::Io);
    assert_eq!(std::fs::read(game.join("kiln/.install.json")).unwrap(), receipt_before);
    assert_eq!(install::read_receipt(&game).unwrap(), Some(old.clone()));
    for f in &old.files {
        if f.path != "kiln/plugins/eml/edited.dll" {
            assert_eq!(hash::sha256_file(&game.join(&f.path)).unwrap().0, f.sha256, "{} restored", f.path);
        }
    }
    assert!(!game.join("kiln/plugins/fresh/fresh.dll").exists());
    assert_eq!(std::fs::read(game.join("kiln/plugins/eml/edited.dll")).unwrap(), b"player edit");
    std::fs::remove_dir(game.join("kiln/.install.json.tmp")).unwrap();
    let r = install::install(&dirs, &client(&game), &b2, &options, &mut NoProgress).unwrap();
    assert_eq!(r.runtime.deselected.as_deref().unwrap(), ["emf", "eml"]);
    assert!(!game.join("kiln/plugins/eml/eml.dll").exists());
    assert_eq!(std::fs::read(game.join("kiln/plugins/eml/edited.dll")).unwrap(), b"player edit");
    assert!(game.join("kiln/plugins/fresh/fresh.dll").is_file());
    assert_eq!(std::fs::read(game.join("kiln/plugins/eml/settings.json")).unwrap(), b"settings");
}

#[test]
fn review_samples_addition_keeps_opt_outs_and_resolves_the_server_selection_under_the_install_lock() {
    let t = tmp("parts-server-samples");
    let dirs = DataDirs::at(t.0.join("data"));
    let game = t.0.join("server");
    put(&game, "enshrouded_server.exe", b"MZ");
    let mut inst = client(&game);
    inst.kind = GameKind::Server;
    inst.exe = "enshrouded_server.exe".into();
    let b1 = bundle::create(&stage(&t.0, "a", false), &t.0.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    install::install(&dirs, &inst, &b1, &InstallOptions {
        components: Some(vec!["core".into(), "sigdb-server".into()]), ..Default::default()
    }, &mut NoProgress).unwrap();
    let s = stage_plus(&t.0, "b", &[("eml", true)]);
    let path = s.join("kiln-runtime.json");
    let mut m: serde_json::Value = serde_json::from_slice(&std::fs::read(&path).unwrap()).unwrap();
    m["components"]["eml"]["requires"] = serde_json::json!(["emf"]);
    std::fs::write(path, serde_json::to_vec(&m).unwrap()).unwrap();
    let b2 = bundle::create(&s, &t.0.join("out"), &opts(1_793_880_000, "bbbbbbb")).unwrap();
    let r = install::install_adding(&dirs, &inst, &b2, &InstallOptions::default(), &["samples".into()], &mut NoProgress).unwrap();
    assert_eq!(comps(&r), ["core", "emf", "eml", "samples", "sigdb-server"]);
    assert_eq!(r.runtime.deselected.as_deref().unwrap(), ["emf"]);
    assert!(game.join("kiln/mods/hello/Hello.dll").is_file());
    assert!(!game.join("kiln/sigdb/client.json").exists());
    // A server-to-client receipt mismatch must fail before changing either files or preference.
    put(&game, "enshrouded.exe", b"MZ");
    let before = std::fs::read(game.join("kiln/.install.json")).unwrap();
    assert_eq!(install::install(&dirs, &client(&game), &b1, &InstallOptions::default(), &mut NoProgress).unwrap_err().code, ErrorCode::Conflict);
    assert_eq!(std::fs::read(game.join("kiln/.install.json")).unwrap(), before);
    assert_eq!(std::fs::read(game.join("kiln/plugins/eml/eml.dll")).unwrap(), b"eml b");
}

/// A file-level inventory, including nested SF config/mods and legacy EML backups (not just the proxy).
fn sf_inventory(game: &Path) -> std::collections::BTreeMap<String, String> {
    fn walk(root: &Path, dir: &Path, out: &mut std::collections::BTreeMap<String, String>) {
        for e in std::fs::read_dir(dir).unwrap().flatten() {
            let p = e.path();
            if p == root.join("kiln") { continue; }
            if e.file_type().unwrap().is_dir() { walk(root, &p, out); }
            else { out.insert(p.strip_prefix(root).unwrap().to_string_lossy().into_owned(), hash::sha256_file(&p).unwrap().0); }
        }
    }
    let mut out = Default::default();
    walk(game, game, &mut out);
    out
}

fn sf_package(root: &Path, id: &str) -> PathBuf {
    let folder = root.join(id);
    put(&folder, "mod.json", format!(r#"{{"id":"{id}","name":"{id}","version":"1.0.0"}}"#).as_bytes());
    put(&folder, "src/mod.lua", b"return {}");
    put(&folder, "extended.mod.json", b"{\r\n  \"enabled\": true,\r\n  \"settings\": {\"speed\": 2}\r\n}\r\n");
    folder
}

fn sf_setup(root: &Path) -> (DataDirs, GameInstall, PathBuf, kiln_manage::profile::Profile) {
    use kiln_manage::{library, profile};
    let dirs = DataDirs::at(root.join("data"));
    let game = root.join("game");
    put(&game, "enshrouded.exe", b"MZ");
    put(&game, "dinput8.dll", &build_test_dll(&["ShroudforgeSetDinput8Provider"], b"original SF"));
    put(&game, "dbghelp.dll", &build_test_dll(&["MiniDumpWriteDump"], b"shroudforge/shroudforge-runtime.dll"));
    put(&game, "winmm.dll", &build_test_dll(&["ShroudforgeStop"], b""));
    put(&game, "shroudforge/shroudforge-runtime.dll", b"player SF runtime");
    put(&game, "shroudforge/config/modloader-config.json", br#"{"paths":{"mods":"SF-user-mods"},"modules":{"updater":false}}"#);
    put(&game, "shroudforge/state.json", br#"{"previousPlayerState":true}"#);
    // An earlier SF/EML writer's working patch and immutable backup must not be made vanilla by uninstall.
    for (name, bytes) in [("enshrouded.kfc", "previous SF patch"), ("enshrouded.kfc_resources", "previous SF resources"),
        ("enshrouded.kfc.bak", "prior vanilla"), ("enshrouded.kfc_resources.bak", "prior resource baseline"),
        ("enshrouded.kfc.bak.integrity", "previous writer seal"), ("enshrouded_000.dat", "original payload")] {
        put(&game, name, bytes.as_bytes());
    }
    sf_package(&game.join("SF-user-mods"), "player-mod");
    let src = sf_package(&root.join("src"), "kiln-added");
    let imported = library::import(&dirs, &[src], &mut NoProgress).unwrap();
    let r = imported.imported.first().expect("SF imported");
    let mut profile = profile::create(&dirs, "SF profile", None).unwrap();
    profile.mods.shroudforge.push(profile::ProfileMod { id: r.id.clone(), version: r.version.clone(), hash: r.hash.clone(), enabled: true });
    profile = profile::save(&dirs, &profile).unwrap();
    let zip = bundle::create(&stage(root, "sf-a", false), &root.join("out"), &opts(1_793_793_600, "aaaaaaa")).unwrap();
    (dirs, client(&game), zip, profile)
}

#[test]
fn sf_restore_install_use_switch_repeat_upgrade_uninstall_file_roundtrip() {
    use kiln_manage::profile;
    let t = tmp("sf-roundtrip");
    let (dirs, gi, zip, p) = sf_setup(&t.0);
    let before = sf_inventory(&gi.path);
    let opts = InstallOptions { switch_from_shroudforge: true, ..Default::default() };
    let first = install::install(&dirs, &gi, &zip, &opts, &mut NoProgress).unwrap();
    let original = first.replaced.iter().find(|r| r.path == "dinput8.dll").unwrap().clone();
    profile::activate(&dirs, &p.id, &gi).unwrap();
    assert!(kiln_manage::shroudforge::read_mod(&gi.path.join("SF-user-mods/kiln-added")).unwrap().enabled);
    let empty = profile::create(&dirs, "Empty", None).unwrap();
    profile::activate(&dirs, &empty.id, &gi).unwrap();
    assert!(!kiln_manage::shroudforge::read_mod(&gi.path.join("SF-user-mods/kiln-added")).unwrap().enabled);
    profile::activate(&dirs, &p.id, &gi).unwrap();
    let twice = install::install(&dirs, &gi, &zip, &opts, &mut NoProgress).unwrap();
    assert_eq!(twice.replaced.iter().find(|r| r.path == "dinput8.dll"), Some(&original), "first backup survives repeat install");
    let upgrade = bundle::create(&stage(&t.0, "sf-b", false), &t.0.join("out"), &self::opts(1_793_880_000, "bbbbbbb")).unwrap();
    let upgraded = install::install(&dirs, &gi, &upgrade, &opts, &mut NoProgress).unwrap();
    assert_eq!(upgraded.replaced.iter().find(|r| r.path == "dinput8.dll"), Some(&original));
    install::uninstall_report(&dirs, &gi, &mut NoProgress).unwrap();
    assert_eq!(sf_inventory(&gi.path), before, "uninstall must undo unchanged placed SF files, not just restore the proxy");
    // A second install/use/uninstall is the same roundtrip, despite retained kiln/ configuration and profiles.
    install::install(&dirs, &gi, &zip, &opts, &mut NoProgress).unwrap();
    profile::activate(&dirs, &p.id, &gi).unwrap();
    install::uninstall_report(&dirs, &gi, &mut NoProgress).unwrap();
    assert_eq!(sf_inventory(&gi.path), before);
}

#[test]
fn sf_restore_deactivate_removes_unchanged_placed_mods_but_not_player_mods() {
    let t = tmp("sf-deactivate");
    let (dirs, gi, zip, p) = sf_setup(&t.0);
    install::install(&dirs, &gi, &zip, &InstallOptions { switch_from_shroudforge: true, ..Default::default() }, &mut NoProgress).unwrap();
    let before = sf_inventory(&gi.path);
    kiln_manage::profile::activate(&dirs, &p.id, &gi).unwrap();
    kiln_manage::profile::deactivate(&dirs, &gi).unwrap();
    assert_eq!(sf_inventory(&gi.path), before, "legacy mode must not keep running a profile's SF mods");
}

#[test]
fn sf_restore_retains_player_edits_unknown_old_receipts_and_unreceipted_markers() {
    use kiln_manage::profile;
    for scenario in ["settings", "added-file", "changed-code", "old-receipt", "unreceipted"] {
        let t = tmp(&format!("sf-conflict-{scenario}"));
        let (dirs, gi, zip, p) = sf_setup(&t.0);
        install::install(&dirs, &gi, &zip, &InstallOptions { switch_from_shroudforge: true, ..Default::default() }, &mut NoProgress).unwrap();
        profile::activate(&dirs, &p.id, &gi).unwrap();
        let dst = gi.path.join("SF-user-mods/kiln-added");
        match scenario {
            "settings" => { kiln_manage::shroudforge::set_setting(&dst, "speed", &serde_json::json!(7)).unwrap(); }
            "added-file" => put(&dst, ".player-settings/private.txt", b"keep me"),
            "changed-code" => put(&dst, "src/mod.lua", b"player code"),
            "old-receipt" | "unreceipted" => {
                let file = gi.path.join("kiln/state/library-placements.json");
                let mut v: serde_json::Value = serde_json::from_slice(&std::fs::read(&file).unwrap()).unwrap();
                if scenario == "old-receipt" { for p in v.as_array_mut().unwrap() { p.as_object_mut().unwrap().remove("extension"); } }
                else { v = serde_json::json!([]); }
                std::fs::write(file, serde_json::to_vec(&v).unwrap()).unwrap();
            }
            _ => unreachable!(),
        }
        let own_before = sf_inventory(&gi.path);
        let report = install::uninstall_report(&dirs, &gi, &mut NoProgress).unwrap();
        assert!(dst.is_dir(), "{scenario}: player files must not disappear");
        for (path, expected) in own_before.iter().filter(|(p, _)| p.starts_with("SF-user-mods/")) {
            assert_eq!(&sf_inventory(&gi.path)[path], expected, "{scenario}: {path} changed");
        }
        if scenario != "unreceipted" {
            assert_eq!(report.placements.retained.len(), 1, "{scenario}: {report:?}");
            assert_eq!(report.placements.retained[0].path, dst);
            assert!(report.placements.retained[0].may_run);
            assert!(report.placements.warning().unwrap().contains("standalone loader"));
        }
    }
}

#[test]
fn sf_restore_settings_remain_player_owned_across_switch_and_mod_upgrade() {
    use kiln_manage::{library, profile};
    let t = tmp("sf-settings-upgrade");
    let (dirs, gi, zip, mut p) = sf_setup(&t.0);
    install::install(&dirs, &gi, &zip, &InstallOptions { switch_from_shroudforge: true, ..Default::default() }, &mut NoProgress).unwrap();
    profile::activate(&dirs, &p.id, &gi).unwrap();
    let dst = gi.path.join("SF-user-mods/kiln-added");
    kiln_manage::shroudforge::set_setting(&dst, "speed", &serde_json::json!(7)).unwrap();
    let empty = profile::create(&dirs, "Empty", None).unwrap();
    profile::activate(&dirs, &empty.id, &gi).unwrap();
    profile::activate(&dirs, &p.id, &gi).unwrap();
    let src = t.0.join("src/kiln-added");
    put(&src, "mod.json", br#"{"id":"kiln-added","name":"Upgrade","version":"2.0.0"}"#);
    let imported = library::import(&dirs, &[src], &mut NoProgress).unwrap();
    let r = &imported.imported[0];
    p.mods.shroudforge[0].version = r.version.clone();
    p.mods.shroudforge[0].hash = r.hash.clone();
    p = profile::save(&dirs, &p).unwrap();
    profile::activate(&dirs, &p.id, &gi).unwrap();
    let report = profile::deactivate_report(&dirs, &gi).unwrap();
    assert_eq!(report.retained.len(), 1);
    assert_eq!(kiln_manage::shroudforge::read_mod(&dst).unwrap().settings[0].value, 7);
    assert_eq!(report.retained[0].path, dst);
}

/// Ownership accepts generated and documented mutable files for reapply, but cleanup must not turn that acceptance
/// into authority to delete them. Exercise both callers on the combined SET/SF receipt implementation.
#[test]
fn cleanup_retains_runtime_settings_on_deactivate_and_uninstall() {
    use kiln_manage::{engine_mods, library, manifest::ModKind, profile};
    for uninstall in [false, true] {
        for kind in [ModKind::Set, ModKind::Eml, ModKind::ShroudForge] {
            for shipped in [false, true] {
                let t = tmp(&format!("cleanup-player-{}-{uninstall}-{shipped}", kind.as_str()));
                let (dirs, gi, zip, mut p) = sf_setup(&t.0);
                let (src, id, rel, dst) = match kind {
                    ModKind::Set => {
                        let src = t.0.join("src/set-player");
                        put(&src, "init.lua", b"return {}");
                        (src, "set-player", "settings.json", gi.path.join("shrouded_engine_tweaks/mods/set-player"))
                    }
                    ModKind::Eml => {
                        let src = t.0.join("src/eml-player");
                        put(&src, "mod.json", br#"{"id":"eml-player","name":"EML Player","version":"1.0.0","capabilities":["patch"]}"#);
                        put(&src, "src/mod.lua", b"return {}");
                        (src, "eml-player", "config.txt", gi.path.join("SF-user-mods/eml-player"))
                    }
                    ModKind::ShroudForge => (t.0.join("src/kiln-added"), "kiln-added", "runtime/settings.json", gi.path.join("SF-user-mods/kiln-added")),
                    _ => unreachable!(),
                };
                // SF has no arbitrary mutable shipped-file exception: its "shipped" case is a changed setting in its
                // shipped extended.mod.json, the other one a runtime file it adds after placement.
                if shipped && kind != ModKind::ShroudForge { put(&src, rel, b"defaults"); }
                let imported = library::import(&dirs, &[src], &mut NoProgress).unwrap();
                let r = imported.imported.iter().chain(&imported.existing).find(|r| r.kind == kind && r.id == id).unwrap();
                *p.mods.list_mut(kind) = vec![profile::ProfileMod { id: r.id.clone(), version: r.version.clone(), hash: r.hash.clone(), enabled: true }];
                p = profile::save(&dirs, &p).unwrap();
                install::install(&dirs, &gi, &zip, &InstallOptions { switch_from_shroudforge: true, ..Default::default() }, &mut NoProgress).unwrap();
                profile::activate(&dirs, &p.id, &gi).unwrap();
                if shipped && kind == ModKind::ShroudForge {
                    kiln_manage::shroudforge::set_setting(&dst, "speed", &serde_json::json!(7)).unwrap();
                } else {
                    put(&dst, rel, b"player runtime settings");
                }
                let folder = sf_inventory(&dst);
                let report = if uninstall {
                    install::uninstall_report(&dirs, &gi, &mut NoProgress).unwrap().placements
                } else {
                    profile::deactivate_report(&dirs, &gi).unwrap()
                };
                assert!(report.retained.iter().any(|r| r.path == dst && r.may_run), "{kind:?}/{shipped}/{uninstall}: {report:?}");
                assert!(!report.removed.contains(&dst));
                assert_eq!(sf_inventory(&dst), folder, "{kind:?}/{shipped}/{uninstall}: the whole folder stays as it was");
                // The receipt still records this as a retained placement; a second cleanup cannot adopt it as disposable.
                let again = engine_mods::unplace(&gi.path).unwrap();
                assert!(again.retained.iter().any(|r| r.path == dst));
                assert_eq!(sf_inventory(&dst), folder);
            }
        }
    }
}

fn sf_legacy_helpers(game: &Path, receipt: &mut install::InstallReceipt) {
    use install::{ReplacedAction, ReplacedClass, ReplacedFile};
    // A real pre-compat receipt shape: old switch-from-SF displaced all three bootstrap/helper DLLs.
    for rel in ["dbghelp.dll", "winmm.dll"] {
        let (sha256, size) = hash::sha256_file(&game.join(rel)).unwrap();
        let backup = format!("kiln/backups/legacy/{rel}");
        put(game, &backup, &std::fs::read(game.join(rel)).unwrap());
        std::fs::remove_file(game.join(rel)).unwrap();
        receipt.replaced.push(ReplacedFile { path: rel.into(), sha256, size: Some(size), class: Some(ReplacedClass::ShroudForge),
            action: ReplacedAction::BackedUp, chain_path: None, backup: Some(backup), engine: None });
    }
    std::fs::write(game.join("kiln/.install.json"), serde_json::to_vec(&receipt).unwrap()).unwrap();
}

#[test]
fn sf_restore_legacy_switch_receipt_repairs_missing_helpers_during_reinstall() {
    let t = tmp("sf-legacy-helpers");
    let (dirs, gi, zip, _) = sf_setup(&t.0);
    let opts = InstallOptions { switch_from_shroudforge: true, ..Default::default() };
    let before = sf_inventory(&gi.path);
    let mut receipt = install::install(&dirs, &gi, &zip, &opts, &mut NoProgress).unwrap();
    sf_legacy_helpers(&gi.path, &mut receipt);
    install::install(&dirs, &gi, &zip, &opts, &mut NoProgress).unwrap();
    for rel in ["dbghelp.dll", "winmm.dll"] {
        assert!(gi.path.join(rel).is_file(), "legacy switch backup must restore {rel} before SF is used again");
        assert_eq!(hash::sha256_file(&gi.path.join(rel)).unwrap().0, before[rel]);
    }
    install::uninstall_report(&dirs, &gi, &mut NoProgress).unwrap();
    assert_eq!(sf_inventory(&gi.path), before);
}

#[test]
fn sf_restore_legacy_helper_recovery_preserves_newer_helpers_and_first_backups() {
    let t = tmp("sf-legacy-conflict");
    let (dirs, gi, zip, _) = sf_setup(&t.0);
    let opts = InstallOptions { switch_from_shroudforge: true, ..Default::default() };
    let mut receipt = install::install(&dirs, &gi, &zip, &opts, &mut NoProgress).unwrap();
    sf_legacy_helpers(&gi.path, &mut receipt);
    let replacement = build_test_dll(&["MiniDumpWriteDump"], b"player updated SF helper");
    put(&gi.path, "dbghelp.dll", &replacement);
    let (updated, report) = install::install_report(&dirs, &gi, &zip, &opts, &mut NoProgress).unwrap();
    assert_eq!(updated.replaced, receipt.replaced);
    assert_eq!(report.restored_shroudforge, ["winmm.dll"]);
    assert_eq!(report.warnings.len(), 1);
    assert!(report.warnings[0].contains("kiln/backups/legacy/dbghelp.dll"));
    let report = install::uninstall_report(&dirs, &gi, &mut NoProgress).unwrap();
    assert_eq!(report.warnings.len(), 1);
    assert_eq!(std::fs::read(gi.path.join("dbghelp.dll")).unwrap(), replacement);
    assert_eq!(hash::sha256_file(&gi.path.join("kiln/backups/legacy/dbghelp.dll")).unwrap().0,
        receipt.replaced.iter().find(|r| r.path == "dbghelp.dll").unwrap().sha256);
}

#[test]
fn sf_restore_legacy_helper_recovery_rolls_back_a_later_failed_install() {
    let t = tmp("sf-legacy-rollback-late-receipt-failure");
    let (dirs, gi, zip, _) = sf_setup(&t.0);
    let opts = InstallOptions { switch_from_shroudforge: true, ..Default::default() };
    let mut receipt = install::install(&dirs, &gi, &zip, &opts, &mut NoProgress).unwrap();
    sf_legacy_helpers(&gi.path, &mut receipt);
    std::fs::create_dir(gi.path.join("kiln/.install.json.tmp")).unwrap();
    let before = sf_inventory(&gi.path);
    let installed_before = std::fs::read(gi.path.join("dinput8.dll")).unwrap();
    let error = install::install(&dirs, &gi, &zip, &opts, &mut NoProgress).unwrap_err();
    assert_eq!(error.code, ErrorCode::Io);
    assert_eq!(sf_inventory(&gi.path), before, "no partially recovered helpers");
    assert_eq!(std::fs::read(gi.path.join("dinput8.dll")).unwrap(), installed_before);
    assert_eq!(install::read_receipt(&gi.path).unwrap(), Some(receipt));
}

/// The helpers are a courtesy restore of files Kiln does not own: a backup that is gone or changed (the player cleaned
/// kiln/backups), with no good copy in the launcher's data folder either, is a warning, never a refused update. With
/// ShroudForge itself removed from the game folder they are not put back at all.
#[test]
fn legacy_shroudforge_helper_backups_never_block_an_update() {
    for scenario in ["missing", "corrupt", "data-copy", "shroudforge-removed"] {
        let t = tmp(&format!("sf-legacy-courtesy-{scenario}"));
        let (dirs, gi, zip, _) = sf_setup(&t.0);
        let opts = InstallOptions { switch_from_shroudforge: true, ..Default::default() };
        let mut receipt = install::install(&dirs, &gi, &zip, &opts, &mut NoProgress).unwrap();
        sf_legacy_helpers(&gi.path, &mut receipt);
        let winmm = std::fs::read(gi.path.join("kiln/backups/legacy/winmm.dll")).unwrap();
        match scenario {
            "missing" => std::fs::remove_file(gi.path.join("kiln/backups/legacy/winmm.dll")).unwrap(),
            "corrupt" => put(&gi.path, "kiln/backups/legacy/winmm.dll", b"corrupt backup"),
            "data-copy" => {
                // the second copy every install backup has in the launcher's data folder
                put(&dirs.backups().join("install").join(&receipt.install_id), "legacy/winmm.dll", &winmm);
                std::fs::remove_file(gi.path.join("kiln/backups/legacy/winmm.dll")).unwrap();
            }
            _ => std::fs::remove_dir_all(gi.path.join("shroudforge")).unwrap(),
        }
        let r = install::install_report(&dirs, &gi, &zip, &opts, &mut NoProgress);
        assert!(r.is_ok(), "{scenario}: {:?}", r.err());
        let (updated, report) = r.unwrap();
        assert_eq!(updated.replaced, receipt.replaced, "{scenario}: the receipt keeps both originals");
        match scenario {
            "missing" | "corrupt" => {
                assert_eq!(report.restored_shroudforge, ["dbghelp.dll"], "{scenario}");
                assert!(!gi.path.join("winmm.dll").exists());
                assert!(report.warnings.iter().any(|w| w.contains("winmm.dll") && w.contains("kiln/backups/legacy/winmm.dll")), "{scenario}: {report:?}");
            }
            "data-copy" => {
                assert_eq!(report.restored_shroudforge, ["dbghelp.dll", "winmm.dll"]);
                assert_eq!(std::fs::read(gi.path.join("winmm.dll")).unwrap(), winmm);
            }
            _ => {
                assert!(report.restored_shroudforge.is_empty(), "{report:?}");
                assert!(!gi.path.join("dbghelp.dll").exists() && !gi.path.join("winmm.dll").exists());
            }
        }
    }
}

#[cfg(unix)]
#[test]
fn sf_restore_cleanup_retains_symlinked_placement_without_touching_its_target() {
    use kiln_manage::profile;
    let t = tmp("sf-cleanup-link");
    let (dirs, gi, zip, p) = sf_setup(&t.0);
    install::install(&dirs, &gi, &zip, &InstallOptions { switch_from_shroudforge: true, ..Default::default() }, &mut NoProgress).unwrap();
    profile::activate(&dirs, &p.id, &gi).unwrap();
    let dst = gi.path.join("SF-user-mods/kiln-added");
    let outside = t.0.join("player-elsewhere");
    std::fs::rename(&dst, &outside).unwrap();
    std::os::unix::fs::symlink(&outside, &dst).unwrap();
    let before = sf_inventory(&outside);
    let report = profile::deactivate_report(&dirs, &gi).unwrap();
    assert_eq!(report.retained.len(), 1);
    assert_eq!(report.retained[0].path, dst);
    assert!(report.retained[0].reason.contains("symlink"));
    assert_eq!(sf_inventory(&outside), before);
    assert!(dst.is_symlink());
}

#[test]
fn sf_restore_player_proxy_replacement_survives_uninstall_with_recovery_path() {
    let t = tmp("sf-proxy-conflict");
    let (dirs, gi, zip, _) = sf_setup(&t.0);
    let receipt = install::install(&dirs, &gi, &zip, &InstallOptions { switch_from_shroudforge: true, ..Default::default() }, &mut NoProgress).unwrap();
    let original = receipt.replaced.iter().find(|r| r.path == "dinput8.dll").unwrap();
    let replacement = build_test_dll(&["ShroudforgeSetDinput8Provider"], b"player updated SF");
    put(&gi.path, "dinput8.dll", &replacement);
    let report = install::uninstall_report(&dirs, &gi, &mut NoProgress).unwrap();
    assert_eq!(std::fs::read(gi.path.join("dinput8.dll")).unwrap(), replacement, "uninstall must not undo the player's newer loader");
    assert!(report.kept.contains(&"dinput8.dll".into()));
    assert_eq!(report.warnings.len(), 1);
    assert!(report.warnings[0].contains(original.backup.as_deref().unwrap()));
    assert_eq!(hash::sha256_file(&gi.path.join(original.backup.as_ref().unwrap())).unwrap().0, original.sha256);
}

#[test]
fn sf_restore_activation_never_changes_a_players_own_sf_package_even_when_profile_switch_differs() {
    use kiln_manage::{library, profile};
    let t = tmp("sf-original-switch");
    let (dirs, gi, zip, mut p) = sf_setup(&t.0);
    install::install(&dirs, &gi, &zip, &InstallOptions { switch_from_shroudforge: true, ..Default::default() }, &mut NoProgress).unwrap();
    let own = gi.path.join("SF-user-mods/player-mod");
    let imported = library::import(&dirs, &[own.clone()], &mut NoProgress).unwrap();
    let r = &imported.imported[0];
    p.mods.shroudforge.push(profile::ProfileMod { id: r.id.clone(), version: r.version.clone(), hash: r.hash.clone(), enabled: false });
    p = profile::save(&dirs, &p).unwrap();
    let before = std::fs::read(own.join("extended.mod.json")).unwrap();
    profile::activate(&dirs, &p.id, &gi).unwrap();
    assert_eq!(std::fs::read(own.join("extended.mod.json")).unwrap(), before);
    assert!(kiln_manage::shroudforge::read_mod(&own).unwrap().enabled);
    profile::deactivate(&dirs, &gi).unwrap();
    install::uninstall_report(&dirs, &gi, &mut NoProgress).unwrap();
    assert_eq!(std::fs::read(own.join("extended.mod.json")).unwrap(), before);
}
