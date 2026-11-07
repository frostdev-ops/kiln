//! `kiln-launcher --selftest [--out <file>] [--keep]` (contracts.md section 9): runs the Rust side
//! headless, before `tauri::Builder`, so the Windows build can be checked under Wine without WebView2.
//! Everything happens in a temp folder with fake trees: Steam discovery, game records, PE classification, a runtime
//! bundle built, verified, installed over an EMF proxy and uninstalled byte-identically, the resolver parity fixtures,
//! JSONC edits (kiln.json), zstd, play-once; on Windows also profile activation with native paths (spaces, non-ASCII)
//! and the process lookup (+ the Steam roots the registry names, only listed). It never touches the real Steam
//! folders, games or launcher data.
//! Exit code 0 = all checks passed. The report is JSON (stdout and `--out`); its `distribution` (and the line
//! `distribution: direct|nexus`) says whether this build updates itself (crate::distribution).

use kiln_manage::game::{self, GameInstall, GameKind, PathMode, Platform};
use kiln_manage::paths::DataDirs;
use kiln_manage::steam::{self, SteamRoot};
use kiln_manage::task::NoProgress;
use kiln_manage::{bundle, install, jsonc, manifest, pe, playonce, resolver};
use serde::Serialize;
use std::fs;
use std::path::{Path, PathBuf};
use std::time::Instant;

include!(concat!(env!("OUT_DIR"), "/fixtures.rs"));

#[derive(Serialize)]
struct Check {
    name: &'static str,
    ok: bool,
    ms: u128,
    detail: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Report {
    format: u32,
    ok: bool,
    launcher: &'static str,
    kiln: &'static str,
    /// "direct" (updates itself) or "nexus" (no self-update): crate::distribution.
    distribution: &'static str,
    build_marker: &'static str,
    platform: &'static str,
    arch: &'static str,
    temp: String,
    passed: usize,
    failed: usize,
    checks: Vec<Check>,
}

type R = Result<String, String>;

fn e<E: std::fmt::Display>(x: E) -> String {
    x.to_string()
}

fn ensure(cond: bool, msg: impl Into<String>) -> Result<(), String> {
    if cond { Ok(()) } else { Err(msg.into()) }
}

fn put(root: &Path, rel: &str, data: &[u8]) -> Result<(), String> {
    let p = root.join(rel);
    fs::create_dir_all(p.parent().unwrap()).map_err(e)?;
    fs::write(p, data).map_err(e)
}

fn vdf(name: &str) -> &'static str {
    VDF_FIXTURES.iter().find(|(n, _)| *n == name).map(|(_, t)| *t).unwrap_or("")
}

fn vdf_path(p: &Path) -> String {
    p.display().to_string().replace('\\', "\\\\")
}

// ---------------------------------------------------------------- checks

fn data_dirs(t: &Path) -> R {
    let d = DataDirs::at(t.join("data"));
    fs::create_dir_all(&d.root).map_err(e)?;
    let s = crate::commands::app::LauncherSettings { saves_keep: 7, ..Default::default() };
    kiln_manage::util::write_json_atomic(&d.settings(), &s).map_err(e)?;
    let back: crate::commands::app::LauncherSettings = kiln_manage::util::read_json(&d.settings()).map_err(e)?.ok_or("settings vanished")?;
    ensure(back.saves_keep == 7, "settings round trip")?;
    let real = DataDirs::locate().map(|x| x.root.display().to_string()).unwrap_or_else(|x| format!("({x})"));
    Ok(format!("settings round trip ok; real data dir would be {real}"))
}

fn discovery(t: &Path) -> R {
    let home = steam::canon(&t.join("home"));
    fs::create_dir_all(&home).map_err(e)?;
    let home = steam::canon(&home);
    let root = home.join("Steam");
    let lib2 = home.join("SteamLibrary");
    fs::create_dir_all(root.join("steamapps/common")).map_err(e)?;
    fs::create_dir_all(lib2.join("steamapps/common")).map_err(e)?;
    put(&root, "steamapps/libraryfolders.vdf", vdf("libraryfolders.vdf").replace("@ROOT@", &vdf_path(&root)).replace("@LIB2@", &vdf_path(&lib2)).as_bytes())?;
    put(&lib2, "steamapps/appmanifest_1203620.acf", vdf("appmanifest_1203620.acf").as_bytes())?;
    put(&root, "steamapps/appmanifest_2278520.acf", vdf("appmanifest_2278520-updating.acf").as_bytes())?;
    put(&lib2, "steamapps/common/Enshrouded/enshrouded.exe", b"MZ selftest client")?;
    put(&root, "steamapps/common/EnshroudedServer/enshrouded_server.exe", b"MZ selftest server")?;
    let roots = [SteamRoot { flavor: steam::flavor_of(&root), path: root.clone() }];
    let data = DataDirs::at(t.join("data-disc"));
    let found = game::detect_in(&roots, &data.root).map_err(e)?;
    let client = found.iter().find(|g| g.kind == GameKind::Client).ok_or_else(|| format!("no client found ({} installs)", found.len()))?;
    ensure(steam::canon(&client.path) == steam::canon(&lib2.join("steamapps/common/Enshrouded")), format!("client path {}", client.path.display()))?;
    ensure(client.build_id.as_deref() == Some("26404966"), format!("build {:?}", client.build_id))?;
    ensure(found.iter().any(|g| g.kind == GameKind::Server), "no server found")?;
    let added = game::add(&data, &client.path).map_err(e)?;
    let known = game::known(&data).map_err(e)?;
    ensure(known.iter().any(|k| k.id == added.id), "added install not in known()")?;
    ensure(added.exe_sha256.as_deref().is_some_and(|h| h.len() == 64), "exe sha256")?;
    Ok(format!("{} installs; client {} ({:?}, {:?}); record {}", found.len(), added.id, added.platform, added.path_mode, data.installs().display()))
}

fn pe_classify(_t: &Path) -> R {
    use pe::ProxyKind as K;
    let cases: [(&str, Vec<u8>, K); 4] = [
        ("kiln", pe::build_test_dll(&["DirectInput8Create", "KilnProxyInfo"], b"KilnProxy/2 0.2.0-dev\0"), K::Kiln),
        ("emf", pe::build_test_dll(&["DirectInput8Create", "emf_loader_identity"], b""), K::Emf),
        ("unknown", pe::build_test_dll(&["DirectInput8Create"], b""), K::Unknown),
        ("notdll", b"MZ not a dll".to_vec(), K::NotDll),
    ];
    for (name, bytes, want) in cases {
        let got = pe::classify_bytes(&bytes).kind;
        ensure(got == want, format!("{name}: got {got:?}, want {want:?}"))?;
    }
    Ok("kiln, emf, unknown, notdll".into())
}

fn stage(root: &Path) -> Result<PathBuf, String> {
    let s = root.join("stage");
    put(&s, "kiln-runtime.json", br#"{
  "format": 1, "kiln": "0.2.0-dev", "abi": 2, "api": "0.2.0", "dotnet": "10.0.14",
  "components": {
    "core": { "sides": ["server", "client"], "default": true, "description": "proxy, core, host" },
    "sigdb-server": { "sides": ["server"], "default": true, "requires": ["core"] },
    "sigdb-client": { "sides": ["client"], "default": true, "requires": ["core"] }
  },
  "builds": { "client": [] },
  "config": ["kiln/kiln.json"]
}"#)?;
    put(&s, "core/dinput8.dll", &pe::build_test_dll(&["DirectInput8Create", "GetdfDIJoystick", "KilnProxyInfo"], b"KilnProxy/2 0.2.0-dev+selftest\0"))?;
    put(&s, "core/kiln/kiln-core.dll", b"selftest core")?;
    put(&s, "core/kiln/kiln.json", b"{\n  // selftest\n  \"policy\": \"verified-only\"\n}\n")?;
    put(&s, "core/kiln/host/Kiln.Host.dll", b"selftest host")?;
    put(&s, "sigdb-client/kiln/sigdb/client.json", b"{\"symbols\":{}}")?;
    put(&s, "sigdb-server/kiln/sigdb/server.json", b"{\"symbols\":{}}")?;
    Ok(s)
}

fn bundle_check(t: &Path) -> Result<(String, PathBuf), String> {
    let s = stage(t)?;
    let opts = bundle::CreateOptions { source_date_epoch: 1_793_793_600, git: "0f1ffa6".into() };
    let a = bundle::create(&s, &t.join("out1"), &opts).map_err(e)?;
    let b = bundle::create(&s, &t.join("out2"), &opts).map_err(e)?;
    ensure(fs::read(&a).map_err(e)? == fs::read(&b).map_err(e)?, "two builds differ")?;
    let m = bundle::verify(&a, &mut NoProgress).map_err(e)?;
    ensure(m.files.iter().any(|f| f.path == "kiln/kiln.json" && f.config), "kiln.json is not a config file")?;
    Ok((format!("{} ({} files, deterministic, verified)", m.version, m.files.len()), a))
}

fn client_install(dir: &Path) -> GameInstall {
    GameInstall {
        id: "selftest-client".into(),
        kind: GameKind::Client,
        path: dir.to_path_buf(),
        exe: "enshrouded.exe".into(),
        app_id: kiln_manage::CLIENT_APP_ID,
        library: None,
        platform: if cfg!(windows) { Platform::Windows } else { Platform::Proton },
        prefix: None,
        build_id: None,
        exe_sha256: None,
        steam_flavor: None,
        path_mode: PathMode::Copy,
        source: "manual".into(),
    }
}

fn install_round_trip(t: &Path, zip: &Path) -> R {
    let dirs = DataDirs::at(t.join("data-inst"));
    let game_dir = t.join("Enshrouded");
    put(&game_dir, "enshrouded.exe", b"MZ selftest game")?;
    let emf = pe::build_test_dll(&["DirectInput8Create", "emf_loader_identity", "emf_loader_play_once"], b"EMF loader");
    put(&game_dir, "dinput8.dll", &emf)?;
    let g = client_install(&game_dir);
    let opts = install::InstallOptions { installer: Some("kiln-launcher selftest".into()), ..Default::default() };
    let r = install::install(&dirs, &g, zip, &opts, &mut NoProgress).map_err(e)?;
    ensure(r.replaced.iter().any(|x| x.class == Some(install::ReplacedClass::Emf)), "the EMF proxy was not recorded as replaced")?;
    let chain = fs::read_to_string(game_dir.join("kiln/chain.ini")).unwrap_or_default();
    ensure(chain.to_ascii_lowercase().contains("emf"), format!("chain.ini has no EMF entry: {chain:?}"))?;
    ensure(pe::classify_proxy(&game_dir.join("dinput8.dll")).map_err(e)?.kind == pe::ProxyKind::Kiln, "dinput8.dll is not Kiln's after install")?;
    let st = install::status(&dirs, &g).map_err(e)?;
    ensure(st.installed && st.modified.is_empty(), format!("status after install: installed {} modified {:?}", st.installed, st.modified))?;
    // the launcher's check after an install: passes now; a file that did not land or another version fails it
    let check = |v: &str| crate::commands::runtime::check_applied(&g, v, &r, &mut NoProgress);
    check(&r.runtime.version).map_err(e)?;
    ensure(check("9.9.9+20990101.0000000").is_err(), "check_applied accepted a version the receipt does not name")?;
    let f = r.files.iter().find(|f| !f.config && f.path != "dinput8.dll").ok_or("the bundle installed no files besides dinput8.dll")?;
    let original = fs::read(game_dir.join(&f.path)).map_err(e)?;
    fs::write(game_dir.join(&f.path), b"not what the bundle brought").map_err(e)?;
    let wrong = check(&r.runtime.version);
    fs::write(game_dir.join(&f.path), &original).map_err(e)?;
    ensure(wrong.as_ref().is_err_and(|x| x.code == kiln_manage::ErrorCode::HashMismatch && x.message.contains("1 file")),
        format!("check_applied with {} changed: {wrong:?}", f.path))?;
    // a part the side cannot take is refused from the manifest alone (the launcher asks before the bundle's verify)
    let m = bundle::read(zip).map_err(e)?;
    ensure(install::select_components(&m, manifest::RunSide::Client, Some(&["core".into(), "sigdb-server".into()])).is_err(), "a client took sigdb-server")?;
    install::uninstall(&dirs, &g, &mut NoProgress).map_err(e)?;
    ensure(fs::read(game_dir.join("dinput8.dll")).map_err(e)? == emf, "uninstall did not restore the EMF proxy byte-identically")?;
    Ok(format!("installed {} over an EMF proxy (chained), uninstalled byte-identically", r.runtime.version))
}

fn resolver_fixtures(_t: &Path) -> R {
    use jsonc::Node;
    let mut n = 0;
    for (name, text) in RESOLVER_FIXTURES.iter().filter(|(n, _)| n.ends_with(".json")) {
        let tree = jsonc::parse_tree(text).map_err(|x| format!("{name}: {x:?}"))?;
        let env = tree.get("env").ok_or(format!("{name}: no env"))?;
        let s = |k: &str| env.get(k).and_then(Node::as_str).map(str::to_string);
        let loaded = env
            .get("loaded")
            .map(|l| l.elements().iter().map(|x| manifest::parse_kiln(x.node.raw(text))).collect::<Result<Vec<_>, _>>())
            .transpose()
            .map_err(|x| format!("{name}: loaded: {x}"))?
            .unwrap_or_default();
        let environment = resolver::Environment {
            kiln: s("kiln").ok_or(format!("{name}: env.kiln"))?,
            api: s("api").ok_or(format!("{name}: env.api"))?,
            side: if s("side").as_deref() == Some("client") { manifest::RunSide::Client } else { manifest::RunSide::Server },
            unavailable_features: env
                .get("unavailableFeatures")
                .map(|a| a.elements().iter().filter_map(|x| x.node.as_str().map(str::to_string)).collect())
                .unwrap_or_default(),
            loaded,
        };
        let candidates: Vec<resolver::Candidate> = tree
            .get("mods")
            .ok_or(format!("{name}: no mods"))?
            .elements()
            .iter()
            .map(|x| {
                let m = &x.node;
                let dir = m.get("dir").and_then(Node::as_str).unwrap_or_default().to_string();
                let raw = match m.get("raw").and_then(Node::as_str) {
                    Some(r) => r.to_string(),
                    None => m.get("manifest").map(|v| v.raw(text).to_string()).unwrap_or_default(),
                };
                match manifest::parse_kiln(&raw) {
                    Ok(man) => resolver::Candidate { dir, manifest: Some(man), error: None },
                    Err(err) => resolver::Candidate { dir, manifest: None, error: Some(err.message) },
                }
            })
            .collect();
        let res = resolver::resolve(&candidates, &environment).map_err(|x| format!("{name}: {x}"))?;
        let doc: serde_json::Value = serde_json::from_str(text).map_err(|x| format!("{name}: {x}"))?;
        let want: Vec<String> = serde_json::from_value(doc["expect"]["order"].clone()).map_err(e)?;
        ensure(res.order == want, format!("{name}: order {:?}, want {:?}", res.order, want))?;
        let mut got: Vec<(String, String, String)> = res.rejected.iter().map(|r| (r.dir.clone(), r.id.clone(), r.reason.clone())).collect();
        got.sort();
        let mut exp: Vec<(String, String, String)> = doc["expect"]["rejected"]
            .as_array()
            .map(|a| a.iter().map(|r| (r["dir"].as_str().unwrap_or("").into(), r["id"].as_str().unwrap_or("").into(), r["reason"].as_str().unwrap_or("").into())).collect())
            .unwrap_or_default();
        exp.sort();
        ensure(got == exp, format!("{name}: rejected {got:?}, want {exp:?}"))?;
        n += 1;
    }
    ensure(n >= 10, format!("only {n} resolver fixtures embedded"))?;
    Ok(format!("{n} fixtures match"))
}

fn kiln_json(t: &Path) -> R {
    let p = t.join("kiln.json");
    fs::write(&p, "{\n  // keep me\n  \"policy\": \"verified-only\",\n  \"log_level\": \"info\"\n}\n").map_err(e)?;
    let d = crate::commands::config::kiln_set(&p, "log_level", Some(&serde_json::json!("debug"))).map_err(e)?;
    ensure(d.values["log_level"] == "debug", format!("log_level = {}", d.values["log_level"]))?;
    ensure(d.text.contains("// keep me"), "the comment was lost")?;
    let d = crate::commands::config::kiln_set(&p, "mods.fault_limit", Some(&serde_json::json!(7))).map_err(e)?;
    ensure(d.values["mods"]["fault_limit"] == 7, "nested key not created")?;
    ensure(crate::commands::config::kiln_set(&p, "mods.fault_limit", Some(&serde_json::json!(1000))).is_err(), "out-of-range value accepted")?;
    let d = crate::commands::config::kiln_set(&p, "log_level", None).map_err(e)?;
    ensure(d.values.get("log_level").is_none(), "reset kept the key")?;
    ensure(d.errors.is_empty(), format!("validation errors {:?}", d.errors))?;
    Ok("set/reset kept comments; range check refused 1000".into())
}

fn zstd(_t: &Path) -> R {
    let data: Vec<u8> = (0..50_000u32).flat_map(|i| (i % 251).to_le_bytes()).collect();
    let z = kiln_manage::saves::editor::zstd_compress(&data).map_err(e)?;
    let back = kiln_manage::saves::editor::zstd_decompress(&z).map_err(e)?;
    ensure(back == data, "round trip differs")?;
    Ok(format!("{} -> {} bytes and back", data.len(), z.len()))
}

fn play_once(_t: &Path) -> R {
    let now = 1_793_793_600;
    let p = playonce::PlayOnce { mode: playonce::PlayOnceMode::NoMods, created: now, nonce: "0123456789abcdef0123456789abcdef".into(), profile: None };
    let text = playonce::render(&p);
    let (st, flag) = playonce::decide(text.as_bytes(), now + 5, false, true);
    ensure(flag.as_ref().is_some_and(|f| f.nonce == p.nonce), format!("decide: {st:?}"))?;
    let (st2, _) = playonce::decide(text.as_bytes(), now + 601, false, true);
    ensure(format!("{st2:?}").to_lowercase().contains("expired"), format!("expiry: {st2:?}"))?;
    Ok("render, decide, expiry".into())
}

/// Windows: profile activation in native path mode, in folders with spaces and non-ASCII names (as under
/// `C:\Users\Jörg Müller\AppData\Local`): profile.json and chain.ini [profile] carry real Windows paths (no Z:, no
/// `\\?\`) that resolve back to the library files; chain.ini is UTF-8 (what the proxy reads first).
#[cfg(windows)]
fn windows_paths(t: &Path) -> R {
    use kiln_manage::manifest::ModKind;
    use kiln_manage::{chain, library, profile};
    let base = steam::strip_verbatim(t.to_path_buf());
    let dirs = DataDirs::at(base.join("Jörg Müller").join("AppData Local").join("Kiln"));
    let src = base.join("mods src");
    put(&src, "new-items/kiln.mod.json", br#"{ "id": "new-items", "version": "1.0.0", "apiVersion": "0.2", "entryAssembly": "M.dll", "side": "both" }"#)?;
    put(&src, "new-items/M.dll", b"selftest")?;
    put(&src, "fps/native.json", br#"{ "format": 1, "id": "fps", "version": "1.0.0", "dlls": ["fps.dll"], "phase": "dllmain", "init": null }"#)?;
    put(&src, "fps/fps.dll", &pe::build_test_dll(&["Whatever"], b"fps"))?;
    let r = library::import(&dirs, &[src.join("new-items"), src.join("fps")], &mut NoProgress).map_err(e)?;
    let refs = |kind: ModKind, id: &str| {
        r.imported.iter().chain(&r.existing).find(|x| x.kind == kind && x.id == id).cloned().ok_or(format!("{id} not imported: {:?}", r.skipped))
    };
    let (k, n) = (refs(ModKind::Kiln, "new-items")?, refs(ModKind::Native, "fps")?);
    let pm = |x: &kiln_manage::library::LibraryRef| profile::ProfileMod { id: x.id.clone(), version: x.version.clone(), hash: x.hash.clone(), enabled: true };
    let mut p = profile::create(&dirs, "Selbsttest Ü", None).map_err(e)?;
    p.mods.kiln = vec![pm(&k)];
    p.mods.native = vec![pm(&n)];
    let p = profile::save(&dirs, &p).map_err(e)?;
    let game_dir = base.join("Steam Bibliothek Ü").join("steamapps").join("common").join("Enshrouded");
    put(&game_dir, "enshrouded.exe", b"MZ selftest game")?;
    fs::create_dir_all(game_dir.join("kiln")).map_err(e)?;
    let mut g = client_install(&game_dir);
    g.path_mode = game::path_mode(&g, &dirs).map_err(e)?;
    ensure(g.path_mode == PathMode::Native, format!("path mode {:?}", g.path_mode))?;
    let a = profile::activate(&dirs, &p.id, &g).map_err(e)?;
    ensure(a.path_mode == PathMode::Native && a.copied == 0, format!("activation {:?}, {} copied", a.path_mode, a.copied))?;
    let f = profile::read_game_profile(&game_dir).map_err(e)?.ok_or("no profile.json")?;
    let kp = &f.mods.kiln[0].path;
    ensure(Path::new(kp) == kiln_manage::library::path_of(&dirs, &k).map_err(e)?.as_path(), format!("kiln path {kp}"))?;
    ensure(kp.as_bytes().get(1) == Some(&b':') && !kp.starts_with("Z:") && !kp.starts_with(r"\\?\") && !kp.contains('/'), format!("not a plain Windows path: {kp}"))?;
    let c = chain::read(&game_dir).map_err(e)?;
    let line = c.profile.first().map(|x| x.path.clone()).ok_or("chain.ini has no [profile] line")?;
    let dll = kiln_manage::winpath::to_host(&line, &g).map_err(e)?;
    ensure(dll.is_file() && line.ends_with("\\fps.dll"), format!("[profile] {line} -> {}", dll.display()))?;
    let raw = fs::read(game_dir.join("kiln/chain.ini")).map_err(e)?;
    ensure(std::str::from_utf8(&raw).is_ok_and(|t| t.contains("Jörg Müller")), "chain.ini is not UTF-8 with the path")?;
    profile::deactivate(&dirs, &g).map_err(e)?;
    Ok(format!("profile.json {kp}; chain.ini [profile] {line}"))
}

/// Windows: the process list finds this exe by its image path (what "the game runs" checks use), case-insensitively.
#[cfg(windows)]
fn processes(_t: &Path) -> R {
    let me = std::env::current_exe().map_err(e)?;
    let (dir, name) = (me.parent().ok_or("no exe folder")?, me.file_name().and_then(|n| n.to_str()).ok_or("exe name")?);
    let p = kiln_manage::proc::find_exe(dir, &name.to_ascii_uppercase()).map_err(e)?.ok_or("own process not found by image path")?;
    ensure(p.pid == std::process::id(), format!("found pid {}, own {}", p.pid, std::process::id()))?;
    ensure(kiln_manage::proc::alive(p.pid), "own pid not alive")?;
    let all = kiln_manage::proc::list().map_err(e)?.len();
    let roots = steam::roots().map(|r| r.iter().map(|x| x.path.display().to_string()).collect::<Vec<_>>().join(", ")).unwrap_or_else(|x| x.to_string());
    Ok(format!("pid {} found ({all} processes); Steam roots (registry, read-only): {}", p.pid, if roots.is_empty() { "none".into() } else { roots }))
}

#[cfg(not(windows))]
fn winpath(_t: &Path) -> R {
    let z = kiln_manage::winpath::unix_to_z(Path::new("/home/player/.local/share/kiln/library")).map_err(e)?;
    ensure(z == "Z:\\home\\player\\.local\\share\\kiln\\library", z.clone())?;
    Ok(z)
}

// ---------------------------------------------------------------- runner

/// The self-update's fixed parts: the sources are https and in order, and this binary knows what it was bundled as
/// (Tauri patches the bundle type into each package's binary; the AppImage and NSIS builds update in place).
#[cfg(feature = "self-update")]
fn self_update(_: &Path) -> R {
    use crate::selfupdate::feed::{self, Channel};
    let s = feed::release_sources();
    let names: Vec<&str> = s.iter().map(|s| s.name.as_str()).collect();
    if names != ["frostdev.io", "GitHub"] {
        return Err(format!("sources {names:?}"));
    }
    for src in &s {
        for ch in [Channel::Stable, Channel::Dev] {
            if !src.url(ch).starts_with("https://") {
                return Err(format!("{}: not https", src.url(ch)));
            }
        }
    }
    let bundle = tauri::utils::platform::bundle_type().map(|b| format!("{b:?}")).unwrap_or_else(|| "none (not a bundle)".into());
    Ok(format!("distribution direct; sources frostdev.io, GitHub (https); bundled as {bundle}"))
}

/// The Nexus Mods build: no self-update compiled in (crate::distribution); the update commands report it disabled.
#[cfg(not(feature = "self-update"))]
fn self_update(_: &Path) -> R {
    use crate::distribution::{Distribution, CURRENT, NEXUS_PAGE};
    ensure(CURRENT == Distribution::Nexus, format!("distribution {}", CURRENT.as_str()))?;
    let bundle = tauri::utils::platform::bundle_type().map(|b| format!("{b:?}")).unwrap_or_else(|| "none (not a bundle)".into());
    Ok(format!("distribution nexus: no self-update in this build, new versions on {NEXUS_PAGE}; bundled as {bundle}"))
}

pub fn run(args: &[String]) -> i32 {
    let out = args.iter().position(|a| a == "--out").and_then(|i| args.get(i + 1)).map(PathBuf::from);
    let keep = args.iter().any(|a| a == "--keep");
    let t = std::env::temp_dir().join(format!("kiln-selftest-{}", std::process::id()));
    let _ = fs::remove_dir_all(&t);
    let mut checks = Vec::new();
    let mut run = |name: &'static str, f: &dyn Fn(&Path) -> R| {
        let dir = t.join(name);
        let start = Instant::now();
        let r = fs::create_dir_all(&dir).map_err(e).and_then(|_| {
            std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| f(&dir))).unwrap_or_else(|p| {
                Err(format!("panicked: {}", p.downcast_ref::<String>().cloned().or_else(|| p.downcast_ref::<&str>().map(|s| s.to_string())).unwrap_or_default()))
            })
        });
        let (ok, detail) = match r {
            Ok(d) => (true, d),
            Err(d) => (false, d),
        };
        println!("{} {name}: {detail}", if ok { "PASS" } else { "FAIL" });
        checks.push(Check { name, ok, ms: start.elapsed().as_millis(), detail });
    };
    run("data_dirs", &data_dirs);
    run("discovery", &discovery);
    run("pe_classify", &pe_classify);
    let zip = std::cell::RefCell::new(None::<PathBuf>);
    run("bundle", &|d| bundle_check(d).map(|(s, z)| { *zip.borrow_mut() = Some(z); s }));
    run("install", &|d| match zip.borrow().as_ref() { Some(z) => install_round_trip(d, z), None => Err("no bundle (bundle check failed)".into()) });
    run("resolver", &resolver_fixtures);
    run("kiln_json", &kiln_json);
    run("zstd", &zstd);
    run("play_once", &play_once);
    run("self_update", &self_update);
    #[cfg(not(windows))]
    run("winpath", &winpath);
    #[cfg(windows)]
    run("windows_paths", &windows_paths);
    #[cfg(windows)]
    run("processes", &processes);

    let failed = checks.iter().filter(|c| !c.ok).count();
    let report = Report {
        format: 1,
        ok: failed == 0,
        launcher: env!("CARGO_PKG_VERSION"),
        kiln: kiln_manage::VERSION,
        distribution: crate::distribution::CURRENT.as_str(),
        build_marker: crate::distribution::build_marker(),
        platform: std::env::consts::OS,
        arch: std::env::consts::ARCH,
        temp: t.display().to_string(),
        passed: checks.len() - failed,
        failed,
        checks,
    };
    let json = serde_json::to_string_pretty(&report).unwrap_or_default();
    if let Some(o) = out {
        if let Err(x) = fs::write(&o, &json) {
            eprintln!("--out {}: {x}", o.display());
            return 2;
        }
    }
    println!("distribution: {}", crate::distribution::CURRENT.as_str());
    println!("{}", if failed == 0 { "selftest: ok" } else { "selftest: FAILED" });
    if !keep {
        let _ = fs::remove_dir_all(&t);
    }
    if failed == 0 { 0 } else { 1 }
}
