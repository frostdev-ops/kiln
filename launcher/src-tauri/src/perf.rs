//! The launcher's backend timing harness (docs/kiln/launcher/perf.md). Two ignored tests, run by tools/launcher/perf.sh:
//! - `perf::generate` builds a realistic fake world in `$KILN_PERF_DIR`: a Steam root with the client (Proton prefix,
//!   saves, localconfig.vdf), the launcher data dir (a Library of ~60 mods over every kind, 5 profiles, 8 runtime
//!   bundles, save backups, a server instance), Kiln installed in the game with its receipt, chain.ini entries and
//!   proxies, `kiln/state/*.json`, a 20 MB kiln.log and a 4 MiB hooks.json. No real game files.
//! - `perf::bench` points the launcher at it (`KILN_DATA_DIR`, `KILN_STEAM_ROOTS`) and times every command the UI
//!   calls (the command function itself where it takes no AppHandle, else the same body), first call and warm calls,
//!   ranked; `$KILN_PERF_OUT` gets the JSON.
//! Never touches the real Steam folders, games or launcher data.

use kiln_manage::game::GameInstall;
use kiln_manage::library::LibraryRef;
use kiln_manage::manifest::ModKind;
use kiln_manage::paths::DataDirs;
use kiln_manage::pe::build_test_dll;
use kiln_manage::profile::ProfileMod;
use kiln_manage::task::NoProgress;
use std::fs;
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

const BUILD_ID: &str = "26430253";
const APP: u32 = kiln_manage::CLIENT_APP_ID;

fn root() -> PathBuf {
    PathBuf::from(std::env::var_os("KILN_PERF_DIR").expect("set KILN_PERF_DIR (tools/launcher/perf.sh does)"))
}

fn put(root: &Path, rel: &str, data: &[u8]) {
    let p = root.join(rel);
    fs::create_dir_all(p.parent().unwrap()).unwrap();
    fs::write(p, data).unwrap();
}

/// Deterministic filler: a 64 KiB pseudo-random block repeated (hashing costs what real bytes cost; deflate stays fast).
fn filler(len: usize, seed: u64) -> Vec<u8> {
    let mut x = seed.wrapping_mul(0x9E37_79B9_7F4A_7C15) | 1;
    let block: Vec<u8> = (0..65536)
        .map(|_| {
            x ^= x << 13;
            x ^= x >> 7;
            x ^= x << 17;
            x as u8
        })
        .collect();
    let mut out = Vec::with_capacity(len);
    out.extend_from_slice(&seed.to_le_bytes());
    while out.len() < len {
        let n = (len - out.len()).min(block.len());
        out.extend_from_slice(&block[..n]);
    }
    out.truncate(len);
    out
}

fn sizes_mb() -> usize {
    std::env::var("KILN_PERF_RUNTIME_MB").ok().and_then(|v| v.parse().ok()).unwrap_or(200)
}

// ---------------------------------------------------------------- the data set

struct World {
    data: DataDirs,
    steam: PathBuf,
    game: PathBuf,
}

fn world() -> World {
    let r = root();
    let steam = r.join("steam");
    World { data: DataDirs::at(r.join("data")), game: steam.join("steamapps/common/Enshrouded"), steam }
}

fn env_on(w: &World) {
    // SAFETY: the bench runs alone (--test-threads=1), before any thread reads these.
    unsafe {
        std::env::set_var("KILN_DATA_DIR", &w.data.root);
        std::env::set_var("KILN_STEAM_ROOTS", &w.steam);
    }
}

fn steam_tree(w: &World) {
    let s = w.steam.display().to_string();
    put(&w.steam, "steamapps/libraryfolders.vdf", format!(
        "\"libraryfolders\"\n{{\n\t\"0\"\n\t{{\n\t\t\"path\"\t\t\"{s}\"\n\t\t\"apps\"\n\t\t{{\n\t\t\t\"{APP}\"\t\t\"60000000000\"\n\t\t}}\n\t}}\n}}\n").as_bytes());
    put(&w.steam, &format!("steamapps/appmanifest_{APP}.acf"), format!(
        "\"AppState\"\n{{\n\t\"appid\"\t\t\"{APP}\"\n\t\"name\"\t\t\"Enshrouded\"\n\t\"StateFlags\"\t\t\"4\"\n\t\"installdir\"\t\t\"Enshrouded\"\n\t\"buildid\"\t\t\"{BUILD_ID}\"\n}}\n").as_bytes());
    // localconfig.vdf: ~60 KB of other apps, then this one with its launch options
    let mut lc = String::from("\"UserLocalConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n\t\t\t\t\"apps\"\n\t\t\t\t{\n");
    for app in 1000..1400u32 {
        lc.push_str(&format!("\t\t\t\t\t\"{app}\"\n\t\t\t\t\t{{\n\t\t\t\t\t\t\"LastPlayed\"\t\t\"1762000000\"\n\t\t\t\t\t\t\"Playtime\"\t\t\"{app}\"\n\t\t\t\t\t\t\"cloud\"\n\t\t\t\t\t\t{{\n\t\t\t\t\t\t\t\"last_sync_state\"\t\t\"synchronized\"\n\t\t\t\t\t\t}}\n\t\t\t\t\t}}\n"));
    }
    lc.push_str(&format!("\t\t\t\t\t\"{APP}\"\n\t\t\t\t\t{{\n\t\t\t\t\t\t\"LaunchOptions\"\t\t\"WINEDLLOVERRIDES=\\\"dinput8=n,b\\\" %command%\"\n\t\t\t\t\t}}\n"));
    lc.push_str("\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n}\n");
    put(&w.steam, "userdata/12345678/config/localconfig.vdf", lc.as_bytes());
    // saves: 12 slots of 0.5-4 MB
    for i in 0..12u64 {
        let name = if i < 10 { format!("3ad85aea_{i}") } else { format!("3ad85aea_{}-1", i - 10) };
        put(&w.steam, &format!("userdata/12345678/{APP}/remote/{name}"), &filler(512 * 1024 + (i as usize % 4) * 1024 * 1024, 100 + i));
    }
    put(&w.steam, &format!("userdata/12345678/{APP}/remote/enshrouded_user.json"), b"{\"version\":1}");
    // the Proton prefix: a 3 MB user.reg with the dinput8 override, z: -> /
    let pfx = w.steam.join(format!("steamapps/compatdata/{APP}/pfx"));
    let mut reg = String::from("WINE REGISTRY Version 2\n;; All keys relative to \\\\User\\\\S-1-5-21-0-0-0-1000\n\n");
    let mut i = 0u32;
    while reg.len() < 3 * 1024 * 1024 {
        reg.push_str(&format!("[Software\\\\Wine\\\\Perf\\\\Key{i}] 1762000000\n#time=1dc4f0000000000\n\"Value{i}\"=\"some setting text {i}\"\n\"Other\"=dword:{i:08x}\n\n"));
        i += 1;
    }
    reg.push_str("[Software\\\\Wine\\\\DllOverrides] 1762000000\n#time=1dc4f0000000000\n\"dinput8\"=\"native,builtin\"\n\n");
    put(&pfx, "user.reg", reg.as_bytes());
    fs::create_dir_all(pfx.join("dosdevices")).unwrap();
    let _ = fs::remove_file(pfx.join("dosdevices/z:"));
    #[cfg(unix)]
    std::os::unix::fs::symlink("/", pfx.join("dosdevices/z:")).unwrap();
}

fn game_tree(w: &World) {
    let g = &w.game;
    put(g, "enshrouded.exe", &filler(2 * 1024 * 1024, 1));
    // an EMF proxy (the install chains it), EML's dbghelp loader and an unknown winmm proxy next to the game
    put(g, "dinput8.dll", &build_test_dll(&["DirectInput8Create", "emf_loader_identity", "emf_loader_play_once"], b"EMF loader"));
    put(g, "dbghelp.dll", &build_test_dll(&["MiniDumpWriteDump", "SymInitialize"], b"Enshrouded Mod Loader"));
    put(g, "winmm.dll", &build_test_dll(&["timeGetTime", "timeBeginPeriod"], b"some proxy"));
    for i in 0..40u64 {
        put(g, &format!("Data/data{i:02}.kfc"), &filler(64 * 1024, 200 + i));
    }
}

/// A runtime stage of `files` files (sizes like the real bundle: a few big pdbs and dotnet libraries, many small
/// files) totalling about `mb` MB; `tiny` keeps every file at a few bytes (older bundles: same manifest size).
fn stage(dir: &Path, files: usize, mb: usize, tiny: bool) {
    let _ = fs::remove_dir_all(dir);
    put(dir, "kiln-runtime.json", br#"{
  "format": 1, "kiln": "0.2.0-dev", "abi": 2, "api": "0.2.0", "dotnet": "10.0.14",
  "components": {
    "core": { "sides": ["server", "client"], "default": true, "description": "proxy, core, host" },
    "dotnet": { "sides": ["server", "client"], "default": true, "requires": ["core"] },
    "sigdb-server": { "sides": ["server"], "default": true, "requires": ["core"] },
    "sigdb-client": { "sides": ["client"], "default": true, "requires": ["core"] }
  },
  "builds": { "client": [] },
  "config": ["kiln/kiln.json"]
}"#);
    put(dir, "core/dinput8.dll", &build_test_dll(&["DirectInput8Create", "GetdfDIJoystick", "KilnProxyInfo"], b"KilnProxy/2 0.2.0-dev+perf\0"));
    put(dir, "core/kiln/kiln.json", b"{\n  \"policy\": \"verified-only\"\n}\n");
    let total = mb * 1024 * 1024;
    let big = total / 3;
    for i in 0..files {
        let (comp, rel) = match i % 10 {
            0..=5 => ("dotnet", format!("kiln/dotnet/shared/Microsoft.NETCore.App/10.0.14/System.Perf{i:04}.dll")),
            6 | 7 => ("core", format!("kiln/plugins/kiln-plugin-{i:04}.dll")),
            8 => ("core", format!("kiln/services/kiln-svc-{i:04}.dll")),
            _ => ("sigdb-client", format!("kiln/sigdb/client-{i:04}.json")),
        };
        let size = if tiny {
            16
        } else if i < 3 {
            big / 3
        } else {
            (total - big) / files
        };
        put(dir, &format!("{comp}/{rel}"), &filler(size, i as u64 * 7 + 3));
    }
    put(dir, "sigdb-server/kiln/sigdb/server.json", b"{\"symbols\":{}}");
}

fn bundles(w: &World) -> PathBuf {
    let tmp = root().join("tmp");
    let mut installed = None;
    for (n, git) in ["1a2b3c4", "2b3c4d5", "3c4d5e6", "4d5e6f7", "5e6f708", "6f70819", "708192a", "8192a3b"].iter().enumerate() {
        let big = n == 6; // the installed one; the last is newer (an update is available)
        let s = tmp.join(format!("stage-{n}"));
        stage(&s, 576, sizes_mb(), !big);
        let opts = kiln_manage::bundle::CreateOptions { source_date_epoch: 1_793_000_000 + n as i64 * 86_400, git: (*git).into() };
        let zip = kiln_manage::bundle::create(&s, &tmp.join(format!("out-{n}")), &opts).unwrap();
        let b = kiln_manage::bundle::import(&w.data, &zip).unwrap();
        if big {
            installed = Some(w.data.runtime().join(b.file.file_name().unwrap()));
        }
        let _ = fs::remove_dir_all(&s);
    }
    installed.unwrap()
}

/// About 60 mods over every kind, as their authors ship them (some in two versions).
fn mod_sources(src: &Path) -> Vec<PathBuf> {
    let _ = fs::remove_dir_all(src);
    let mut out = Vec::new();
    for i in 0..30u64 {
        for v in 0..(if i % 5 == 0 { 2 } else { 1 }) {
            let d = src.join(format!("kiln-{i}-{v}"));
            let id = format!("perf-kiln-{i:02}");
            put(&d, "kiln.mod.json", format!(r#"{{ "id": "{id}", "name": "Perf Kiln Mod {i}", "version": "1.{v}.0", "apiVersion": "0.2", "entryAssembly": "M{i}.dll", "side": "both", "kilnBuild": {{ "version": "0.2.0-dev+20261108.c6ffaec" }} }}"#).as_bytes());
            put(&d, &format!("M{i}.dll"), &filler(200 * 1024 + (i as usize % 7) * 300 * 1024, 1000 + i * 10 + v));
            put(&d, &format!("M{i}.pdb"), &filler(100 * 1024, 2000 + i));
            if i % 3 == 0 {
                for a in 0..30u64 {
                    put(&d, &format!("assets/icons/icon{a:02}.png"), &filler(8 * 1024, 3000 + i * 100 + a));
                }
            }
            out.push(d);
        }
    }
    for i in 0..10u64 {
        let d = src.join(format!("native-{i}"));
        let id = format!("perf-native-{i:02}");
        let phase = if i % 2 == 0 { "dllmain" } else { "boot" };
        let init = if phase == "dllmain" { "null" } else { "\"kiln_native_init\"" };
        put(&d, "native.json", format!(r#"{{ "format": 1, "id": "{id}", "version": "1.0.0", "dlls": ["{id}.dll"], "phase": "{phase}", "init": {init}, "after": [] }}"#).as_bytes());
        put(&d, &format!("{id}.dll"), &build_test_dll(&["kiln_native_init"], id.as_bytes()));
        out.push(d);
    }
    for i in 0..6u64 {
        let d = src.join(format!("emf-{i}"));
        let id = format!("perfemf{i}");
        put(&d, "mod.json", format!(r#"{{ "emf": 1, "id": "{id}", "name": "Perf EMF {i}", "version": "1.0.{i}", "dll": "{id}.dll" }}"#).as_bytes());
        put(&d, &format!("{id}.dll"), &build_test_dll(&["emf_mod_init"], id.as_bytes()));
        out.push(d);
    }
    for i in 0..4u64 {
        let d = src.join(format!("eml-{i}"));
        put(&d, "mod.json", format!(r#"{{"id":"perf-eml-{i}","name":"Perf EML {i}","version":"1.2.{i}","capabilities":["patch"]}}"#).as_bytes());
        put(&d, "src/mod.lua", b"return {}");
        for f in 0..30u64 {
            put(&d, &format!("data/table{f:02}.json"), &filler(4 * 1024, 4000 + i * 100 + f));
        }
        out.push(d);
    }
    for i in 0..3u64 {
        let d = src.join(format!("sf-{i}"));
        put(&d, "mod.json", format!(r#"{{"id":"perf-sf-{i}","name":"Perf SF {i}","version":"1.0.0"}}"#).as_bytes());
        put(&d, "extended.mod.json", br#"{"launcher":"SF","enabled":false,"targets":["client","server"],"settings":{"speed":{"label":"Speed","control":"slider","min":1,"max":10,"value":3}}}"#);
        put(&d, "src/mod.lua", b"return {}");
        out.push(d);
    }
    for i in 0..3u64 {
        let d = src.join(format!("set-{i}/perf-set-{i}"));
        put(&d, "init.lua", b"print('set')");
        put(&d, "mod.json", format!(r#"{{"id":"perf-set-{i}","version":"1.0.0"}}"#).as_bytes());
        out.push(d);
    }
    for i in 0..2u64 {
        let d = src.join(format!("ge-{i}"));
        put(&d, "mod.json", format!(r#"{{"id":"perf-ge-{i}","name":"Perf Greeneye {i}","version":"1.0.0","dll":"ge{i}.dll","side":"client"}}"#).as_bytes());
        put(&d, &format!("ge{i}.dll"), &build_test_dll(&["GreeneyeModStart"], b""));
        out.push(d);
    }
    let fl = src.join("flame");
    put(&fl, "mod.json", br#"{"id":"perf-flame","name":"Perf Flame","version":"1.4.6"}"#);
    put(&fl, "flame.json", br#"{"dll":"creative.dll"}"#);
    put(&fl, "creative.dll", &build_test_dll(&["FlameStartRuntime"], b""));
    out.push(fl);
    put(src, "st/perfmap.dll", &build_test_dll(&["CreateModInstance"], b""));
    out.push(src.join("st/perfmap.dll"));
    out
}

fn pm(r: &LibraryRef) -> ProfileMod {
    ProfileMod { id: r.id.clone(), version: r.version.clone(), hash: r.hash.clone(), enabled: true }
}

fn state_files(w: &World) {
    let g = &w.game;
    let ex = |n: &str| fs::read_to_string(Path::new(env!("CARGO_MANIFEST_DIR")).join("../../schema/examples").join(n)).unwrap();
    put(g, "kiln/state/boot.json", ex("state-boot.json").as_bytes());
    put(g, "kiln/state/native.json", ex("state-native.json").as_bytes());
    put(g, "kiln/state/last-server.json", ex("state-last-server.json").as_bytes());
    // mods.json: one line per Library mod
    let mut mods: serde_json::Value = serde_json::from_str(&ex("state-mods.json")).unwrap();
    let one = mods["mods"][0].clone();
    mods["mods"] = (0..40).map(|i| { let mut m = one.clone(); m["id"] = format!("perf-kiln-{:02}", i % 30).into(); m["loadIndex"] = i.into(); m }).collect();
    put(g, "kiln/state/mods.json", serde_json::to_vec_pretty(&mods).unwrap().as_slice());
    // hooks.json: the example's targets repeated to ~4 MiB
    let mut hooks: serde_json::Value = serde_json::from_str(&ex("hooks-state.json")).unwrap();
    let targets = hooks["targets"].as_array().unwrap().clone();
    let mut all = Vec::new();
    let mut n = 0u64;
    while serde_json::to_vec(&all).unwrap().len() < 4 * 1024 * 1024 {
        for t in &targets {
            let mut t = t.clone();
            t["rva"] = format!("0x{:x}", 0x100000 + n * 0x40).into();
            all.push(t);
            n += 1;
        }
        if n % 3000 == 0 { continue; }
    }
    hooks["targets"] = all.into();
    put(g, "kiln/state/hooks.json", serde_json::to_vec_pretty(&hooks).unwrap().as_slice());
    // kiln.log: 20 MB of the run that wrote boot.json (pid 412), some errors; the previous run's log is older
    let logs = g.join("kiln/logs");
    put(&logs, "kiln.prev.log", &b"[I 01:00:00.000] [kiln] Kiln 0.2.0-dev starting (pid 300)\n".repeat(1));
    let past = std::time::SystemTime::now() - Duration::from_secs(3600);
    fs::File::options().write(true).open(logs.join("kiln.prev.log")).unwrap().set_modified(past).unwrap();
    fs::File::options().write(true).open(g.join("kiln/state/boot.json")).unwrap().set_modified(past + Duration::from_secs(60)).unwrap();
    let mut log = String::from("[I 02:00:00.000] [kiln] Kiln 0.2.0-dev+20261108.708192a starting (pid 412)\n");
    let mut i = 0u64;
    while log.len() < 20 * 1024 * 1024 {
        let lvl = if i % 997 == 0 { "E" } else if i % 101 == 0 { "W" } else { "I" };
        log.push_str(&format!("[{lvl} 02:{:02}:{:02}.{:03}] [kiln-svc-{:02}] tick {i}: something happened with entity {} in zone {}\n", (i / 60000) % 60, (i / 1000) % 60, i % 1000, i % 17, i * 31, i % 9));
        i += 1;
    }
    put(&logs, "kiln.log", log.as_bytes());
    put(&logs, "eml.log", &log.as_bytes()[..256 * 1024]);
    put(&logs, "proxy.log", b"[proxy] loaded chain\n");
    put(&logs, "tick-stats.jsonl", &b"{\"t\":1,\"service\":\"kiln creature brains\",\"ms\":0.4}\n".repeat(4000));
    put(&w.data.root, "logs/launcher.log", &log.as_bytes()[..2 * 1024 * 1024]);
}

#[test]
#[ignore]
fn generate() {
    let w = world();
    let started = Instant::now();
    let _ = fs::remove_dir_all(root());
    fs::create_dir_all(&w.data.root).unwrap();
    env_on(&w);
    steam_tree(&w);
    game_tree(&w);
    let g: GameInstall = kiln_manage::game::add(&w.data, &w.game).unwrap();
    println!("game {} ({:?}, prefix {:?})", g.id, g.platform, g.prefix);
    let zip = bundles(&w);
    let opts = kiln_manage::install::InstallOptions { installer: Some("perf".into()), ..Default::default() };
    kiln_manage::install::install(&w.data, &g, &zip, &opts, &mut NoProgress).unwrap();
    // the game record again (the receipt names the install now)
    let g = kiln_manage::game::add(&w.data, &w.game).unwrap();
    // [chain]: three more closed DLL mods
    for (n, marker) in [("xhl-core.dll", &b"xhl"[..]), ("fov-tweak.dll", b"fov"), ("chain-loader-dinput8.dll", b"reads dinput8-chain.ini")] {
        put(&w.game, &format!("kiln/chain/{n}"), &build_test_dll(&[], marker));
        kiln_manage::chain::add_chain_entry(&w.game, &format!("chain\\{n}")).unwrap();
    }
    // the Library
    let src = root().join("tmp/mods");
    let report = kiln_manage::library::import(&w.data, &mod_sources(&src), &mut NoProgress).unwrap();
    assert!(report.skipped.is_empty(), "{:?}", report.skipped);
    let refs: Vec<LibraryRef> = report.imported.clone();
    println!("library: {} imported", refs.len());
    // 5 profiles; the first holds most of the Library and is active in the game
    let mut first = None;
    for (n, name) in ["Main", "Testing", "Vanilla plus", "Server mods", "Old run"].iter().enumerate() {
        let mut p = kiln_manage::profile::create(&w.data, name, None).unwrap();
        for r in refs.iter().enumerate().filter(|(i, _)| n == 0 || (i + n) % (n + 1) == 0).map(|(_, r)| r) {
            let list = p.mods.list_mut(r.kind);
            if !list.iter().any(|m| m.id == r.id) {
                list.push(pm(r));
            }
        }
        // one dllmain native loads late
        if let Some(r) = refs.iter().find(|r| r.kind == ModKind::Native) {
            p.late.push(r.id.clone());
        }
        let p = kiln_manage::profile::save(&w.data, &p).unwrap();
        first.get_or_insert(p.id);
    }
    let active = kiln_manage::profile::activate(&w.data, first.as_ref().unwrap(), &g).unwrap();
    println!("active profile {} (generation {})", active.profile_id, active.generation);
    state_files(&w);
    // save backups
    for i in 0..8 {
        kiln_manage::saves::backup(&w.data, &g, kiln_manage::saves::BackupReason::Manual, Some(&format!("perf {i}"))).unwrap();
    }
    // a local server instance
    let srv = root().join("servers/perf-server");
    put(&srv, "enshrouded_server.exe", &filler(1024 * 1024, 9));
    put(&srv, "enshrouded_server.json", br#"{"name":"Perf Server","saveDirectory":"./savegame","logDirectory":"./logs","ip":"0.0.0.0","queryPort":15637,"slotCount":16}"#);
    for i in 0..4u64 {
        put(&srv, &format!("savegame/3ad85aea_{i}"), &filler(1024 * 1024, 50 + i));
    }
    put(&srv, "logs/enshrouded_server.log", &filler(2 * 1024 * 1024, 77));
    match kiln_manage::server::instance::import(&srv, "Perf Server") {
        Ok(s) => println!("server {}", s.id),
        Err(e) => println!("server import: {e}"),
    }
    let _ = fs::remove_dir_all(root().join("tmp"));
    println!("generated in {:.1}s at {}", started.elapsed().as_secs_f64(), root().display());
}

// ---------------------------------------------------------------- timing

struct Row {
    name: String,
    first: Duration,
    median: Duration,
    max: Duration,
    /// CPU time of the whole process (every thread: commands run on the blocking pool), median of the warm calls.
    cpu: Duration,
    note: String,
}

/// CPU time of this process so far: the sum of every thread's schedstat (Linux, nanoseconds); zero elsewhere.
fn cpu_now() -> Duration {
    let mut ns = 0u64;
    for t in fs::read_dir("/proc/self/task").into_iter().flatten().flatten() {
        if let Ok(s) = fs::read_to_string(t.path().join("schedstat")) {
            ns += s.split_whitespace().next().and_then(|v| v.parse::<u64>().ok()).unwrap_or(0);
        }
    }
    Duration::from_nanos(ns)
}

fn iters() -> usize {
    std::env::var("KILN_PERF_ITERS").ok().and_then(|v| v.parse().ok()).unwrap_or(10)
}

fn time<T>(rows: &mut Vec<Row>, name: &str, mut f: impl FnMut() -> Result<T, kiln_manage::Error>) {
    let t = Instant::now();
    let r = f();
    let first = t.elapsed();
    let note = match &r {
        Ok(_) => String::new(),
        Err(e) => format!("error: {}", e.message),
    };
    let (mut warm, mut cpus): (Vec<Duration>, Vec<Duration>) = (0..iters())
        .map(|_| {
            let c = cpu_now();
            let t = Instant::now();
            let _ = f();
            (t.elapsed(), cpu_now().saturating_sub(c))
        })
        .unzip();
    warm.sort();
    cpus.sort();
    let median = warm.get(warm.len() / 2).copied().unwrap_or(first);
    let max = warm.last().copied().unwrap_or(first);
    let cpu = cpus.get(cpus.len() / 2).copied().unwrap_or_default();
    println!("{name:<44} first {:>9.2} ms  median {:>9.2} ms  max {:>9.2} ms  cpu {:>9.2} ms {note}", ms(first), ms(median), ms(max), ms(cpu));
    rows.push(Row { name: name.into(), first, median, max, cpu, note });
}

fn ms(d: Duration) -> f64 {
    d.as_secs_f64() * 1000.0
}

fn run<F: std::future::Future>(f: F) -> F::Output {
    tauri::async_runtime::block_on(f)
}

#[test]
#[ignore]
fn bench() {
    use crate::commands::*;
    let w = world();
    assert!(w.data.root.is_dir(), "no data set at {}: run perf::generate first", root().display());
    env_on(&w);
    // as the launcher's setup does
    kiln_manage::filecache::persist_at(&crate::state::file_hashes(&w.data));
    let rows = &mut Vec::new();
    let g = kiln_manage::game::known(&w.data).unwrap().into_iter().find(|g| g.kind == kiln_manage::game::GameKind::Client).unwrap();
    let gid = g.id.clone();
    let profiles = kiln_manage::profile::list(&w.data).unwrap();
    let pid = kiln_manage::profile::read_game_profile(&w.game).unwrap().unwrap().id;
    let lib = kiln_manage::library::list(&w.data, None).unwrap();
    let a_ref = lib.iter().find(|e| e.kind == ModKind::Kiln).map(|e| LibraryRef {
        kind: e.kind, id: e.id.clone(), version: e.versions[0].version.clone(), hash: e.versions[0].hash.clone(),
    }).unwrap();
    let server = kiln_manage::server::instance::list().unwrap_or_default().into_iter().next();
    println!("data set: {} profiles, {} library entries, game {gid}, active {pid}, server {:?}", profiles.len(), lib.len(), server.as_ref().map(|s| &s.id));
    let game_t = || logs::LogTarget::Game(gid.clone());
    let cfg_t = || kiln_manage::config::ConfigTarget::Profile(pid.clone());

    // the polls first (what an idle launcher pays), in the order the shell mounts them
    time(rows, "game_status", || run(game::game_status(gid.clone())));
    time(rows, "launch_state", || run(launch::launch_state(gid.clone())));
    time(rows, "runtime_list (bundle::list)", || kiln_manage::bundle::list(&crate::state::dirs()?));
    time(rows, "modpack_pending", || modpack::pending_for(&gid));
    time(rows, "app_assets_status", app::assets_status);
    time(rows, "engine_status", || run(engines::engine_status(gid.clone())));
    time(rows, "watcher tick (find_game + mtimes)", || {
        let _ = crate::watch::game_pid(&g, None);
        for f in [kiln_manage::paths::game::BOOT_JSON, kiln_manage::paths::game::MODS_JSON, kiln_manage::paths::game::NATIVE_JSON, kiln_manage::paths::game::PROFILE] {
            let _ = fs::metadata(g.path.join(f));
        }
        Ok(())
    });
    time(rows, "logs_list", || run(logs::logs_list(game_t())));
    // game_status by part (what one poll pays for)
    time(rows, "  part: steam_roots", || Ok(crate::state::steam_roots()));
    time(rows, "  part: proc::list", kiln_manage::proc::list);
    time(rows, "  part: install::read_receipt", || kiln_manage::install::read_receipt(&g.path));
    let receipt = kiln_manage::install::read_receipt(&g.path).unwrap().unwrap();
    time(rows, "  part: install::modified_files", || Ok(kiln_manage::install::modified_files(&g.path, &receipt)));
    time(rows, "  part: pe::classify_proxy", || kiln_manage::pe::classify_proxy(&g.path.join("dinput8.dll")));
    time(rows, "  part: install::status", || kiln_manage::install::status(&w.data, &g));
    let st = kiln_manage::install::status(&w.data, &g).unwrap();
    time(rows, "  part: game::newer_mods", || Ok(kiln_manage::game::newer_mods(&w.data, &st, Some(&pid))));
    let roots = crate::state::steam_roots();
    time(rows, "  part: game::launch_options_info", || Ok(kiln_manage::game::launch_options_info(&g, &roots)));
    // the rest: what pages ask for when they open
    time(rows, "app_settings_get", || run(app::app_settings_get()));
    time(rows, "game_list", || run(game::game_list()));
    time(rows, "runtime_status", || run(runtime::runtime_status(gid.clone())));
    time(rows, "profile_list", || run(profile::profile_list()));
    time(rows, "profile_get", || run(profile::profile_get(pid.clone())));
    time(rows, "profile_active", || run(profile::profile_active(gid.clone())));
    time(rows, "profile_resolve (client)", || run(profile::profile_resolve(pid.clone(), kiln_manage::manifest::RunSide::Client)));
    time(rows, "library_list (all)", || run(library::library_list(None)));
    time(rows, "library_list (kiln)", || run(library::library_list(Some(ModKind::Kiln))));
    time(rows, "library_unused", || run(library::library_unused()));
    time(rows, "library_users", || run(library::library_users(a_ref.clone())));
    time(rows, "library_lint (profile)", || run(library::library_lint(None, Some(pid.clone()))));
    time(rows, "config_list (profile)", || run(config::config_list(cfg_t())));
    time(rows, "config_native_state", || run(config::config_native_state(cfg_t())));
    time(rows, "config_kiln_read", || run(config::config_kiln_read(gid.clone())));
    let log_name = kiln_manage::logs::list(&g.path).unwrap().into_iter().map(|f| f.name).find(|n| n.ends_with("kiln.log")).unwrap();
    time(rows, "logs_read kiln.log (first 256 KiB)", || run(logs::logs_read(game_t(), log_name.clone(), None, Some(256 * 1024))));
    time(rows, "logs_read kiln.log (whole, 20 MB)", || run(logs::logs_read(game_t(), log_name.clone(), None, None)));
    time(rows, "logs_crash_reports", || run(logs::logs_crash_reports(game_t())));
    time(rows, "saves_list", || run(saves::saves_list(gid.clone())));
    time(rows, "saves_backups", || run(saves::saves_backups(gid.clone())));
    time(rows, "chain_view", || run(compat::chain_view(pid.clone(), gid.clone())));
    let view = run(compat::chain_view(pid.clone(), gid.clone())).unwrap();
    let entry = view.entries.iter().find(|e| e.entry.section == kiln_manage::compat::Section::Chain && !e.loader && !e.entry.is_kiln_own()).map(|e| e.entry.path.clone());
    if let Some(path) = entry {
        let mut late = false;
        time(rows, "chain_set_late ([chain] entry)", || {
            late = !late;
            compat::set_late(&pid, &gid, &path, late).map(|(v, _)| v)
        });
    }
    let prof_entry = view.entries.iter().find(|e| e.entry.section == kiln_manage::compat::Section::Profile).map(|e| e.entry.path.clone());
    if let Some(path) = prof_entry {
        let mut late = true;
        time(rows, "chain_set_late ([profile] entry)", || {
            late = !late;
            compat::set_late(&pid, &gid, &path, late).map(|(v, _)| v)
        });
    }
    let p = kiln_manage::profile::get(&w.data, &pid).unwrap();
    // the chain view and activation by part
    time(rows, "  part: profile::render", || kiln_manage::profile::render(&w.data, &p, &g, kiln_manage::game::path_mode(&g, &w.data)?));
    let file = kiln_manage::profile::render(&w.data, &p, &g, kiln_manage::game::path_mode(&g, &w.data).unwrap()).unwrap();
    let dlls = kiln_manage::compat::dllmain_entries(&w.data, &p, &file).unwrap();
    time(rows, "  part: compat::plan", || Ok(kiln_manage::compat::plan(&w.data, &p, &g, &dlls)));
    time(rows, "  part: compat::read_hooks", || kiln_manage::compat::read_hooks(&g.path));
    time(rows, "  part: engines::scan_proxies", || Ok(kiln_manage::engines::scan_proxies(&g.path)));
    time(rows, "  part: profile::refresh_active", || kiln_manage::profile::refresh_active(&w.data, &g));
    time(rows, "  part: engine_mods::place", || kiln_manage::engine_mods::place(&w.data, &p, &g.path));
    time(rows, "profile_save (active profile)", || {
        let d = crate::state::dirs()?;
        let saved = kiln_manage::profile::save(&d, &p)?;
        Ok((saved.clone(), profile::reapply_where_active(&d, &saved.id)))
    });
    time(rows, "profile_activate", || {
        let g = crate::state::game(&gid)?;
        crate::state::refuse_running(&g)?;
        kiln_manage::profile::activate(&crate::state::dirs()?, &pid, &g)
    });
    if let Some(s) = &server {
        time(rows, "server_list", || run(server::server_list()));
        time(rows, "server_status", || run(server::server_status(s.id.clone())));
        time(rows, "server_mods", || run(server::server_mods(s.id.clone())));
        time(rows, "server_backups", || run(server::server_backups(s.id.clone())));
        time(rows, "server_settings_get", || run(server::server_settings_get(s.id.clone())));
    }

    // the game running: a stand-in process named enshrouded.exe in the game folder (a copy of sleep)
    let bin = root().join("bin");
    fs::create_dir_all(&bin).unwrap();
    let fake = bin.join(&g.exe);
    if !fake.is_file() {
        fs::copy("/bin/sleep", &fake).unwrap();
    }
    if let Ok(mut child) = std::process::Command::new(&fake).arg("600").current_dir(&g.path).spawn() {
        std::thread::sleep(Duration::from_millis(300));
        let pid = crate::watch::game_pid(&g, None);
        assert_eq!(pid, Some(child.id()), "the stand-in game is found");
        time(rows, "watcher tick, game running", || Ok(crate::watch::game_pid(&g, pid)));
        time(rows, "game_status, game running", || run(game::game_status(gid.clone())));
        time(rows, "launch_state, game running", || run(launch::launch_state(gid.clone())));
        let _ = child.kill();
        let _ = child.wait();
    }

    rows.sort_by(|a, b| b.cpu.cmp(&a.cpu).then(b.median.cmp(&a.median)));
    println!("\nranked by CPU per warm call (wall-clock median, first call):");
    for r in rows.iter() {
        println!("{:<44} cpu {:>8.2} ms  wall {:>8.2} ms  first {:>9.2} ms {}", r.name, ms(r.cpu), ms(r.median), ms(r.first), r.note);
    }
    if let Some(out) = std::env::var_os("KILN_PERF_OUT") {
        let v: Vec<serde_json::Value> = rows
            .iter()
            .map(|r| serde_json::json!({ "name": r.name, "firstMs": ms(r.first), "medianMs": ms(r.median), "maxMs": ms(r.max), "cpuMs": ms(r.cpu), "note": r.note }))
            .collect();
        fs::write(out, serde_json::to_vec_pretty(&v).unwrap()).unwrap();
    }
}
