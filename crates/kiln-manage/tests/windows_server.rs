//! A dedicated server the Windows way: the Windows build of kiln-manage installs Kiln's
//! runtime bundle into a native server folder, imports it as a `windows` server, starts `enshrouded_server.exe` with
//! the Windows runner (`server::run`), checks boot.json, A2S and the KAP admin connection, stops it and uninstalls.
//! Needs a real server install, so it is ignored by default. Under Wine (a scratch prefix, never the game's or
//! Steam's):
//!
//! ```sh
//! KILN_DATA_DIR='C:\kiln-data' KILN_WINSRV_DIR='C:\Games\Enshrouded Server Ü' KILN_WINSRV_BUNDLE='Z:\...\kiln-runtime-….zip' \
//!   wine windows_server-<hash>.exe --ignored --nocapture windows_server_lifecycle
//! ```
//! On a Windows PC the same with real paths. `APPDATA` (instance files) and `KILN_DATA_DIR` (server records) should
//! point at scratch folders. `KILN_WINSRV_VANILLA=1` adds a start/stop of the vanilla server after the uninstall
//! (no Kiln.Admin: the stop has to reach the game's own Ctrl-C handler).
#![cfg(all(windows, feature = "server", feature = "admin", feature = "zip"))]

use kiln_manage::admin::client::{AdminClient, Transport};
use kiln_manage::game::{self, GameKind, PathMode, Platform};
use kiln_manage::install::{self, InstallOptions};
use kiln_manage::paths::DataDirs;
use kiln_manage::server::instance::{self, ServerRunner};
use kiln_manage::server::{ServerState, run};
use kiln_manage::task::Progress;
use std::path::PathBuf;
use std::time::{Duration, Instant};

/// eprintln! plus a copy in `$KILN_WINSRV_LOG` (a file keeps working when the stop switches consoles under Wine).
macro_rules! say {
    ($($t:tt)*) => {{
        let line = format!($($t)*);
        eprintln!("{line}");
        if let Some(f) = std::env::var_os("KILN_WINSRV_LOG") {
            use std::io::Write;
            if let Ok(mut f) = std::fs::OpenOptions::new().create(true).append(true).open(f) {
                let _ = writeln!(f, "{line}");
            }
        }
    }};
}

struct Print(Instant);
impl Progress for Print {
    fn report(&mut self, fraction: Option<f32>, message: &str) {
        say!("  [{:6.1} s] {:>4} {message}", self.0.elapsed().as_secs_f32(), fraction.map(|f| format!("{:.0}%", f * 100.0)).unwrap_or_default());
    }
    fn canceled(&self) -> bool {
        false
    }
}

fn env_path(name: &str) -> Option<PathBuf> {
    std::env::var_os(name).filter(|v| !v.is_empty()).map(PathBuf::from)
}

#[test]
#[ignore = "needs a server install: KILN_WINSRV_DIR, KILN_WINSRV_BUNDLE, KILN_DATA_DIR"]
fn windows_server_lifecycle() {
    let (Some(dir), Some(bundle)) = (env_path("KILN_WINSRV_DIR"), env_path("KILN_WINSRV_BUNDLE")) else {
        panic!("set KILN_WINSRV_DIR (server folder) and KILN_WINSRV_BUNDLE (runtime zip)");
    };
    assert!(std::env::var_os("KILN_DATA_DIR").is_some(), "KILN_DATA_DIR must point at a scratch folder");
    let dirs = DataDirs::locate().unwrap();
    let mut p = Print(Instant::now());

    // 1. the folder as the launcher sees it: a native Windows server
    let g = game::inspect(&dir).unwrap();
    say!("inspect: {} {:?} {:?} {:?} id {}", g.path.display(), g.kind, g.platform, g.path_mode, g.id);
    assert_eq!((g.kind, g.platform, g.path_mode, g.prefix.as_ref()), (GameKind::Server, Platform::Windows, PathMode::Native, None));
    assert!(!g.path.to_string_lossy().starts_with(r"\\?\"), "{}", g.path.display());

    // 2. install the runtime bundle (server components)
    let opts = InstallOptions { installer: Some("kiln-manage windows_server test".into()), ..Default::default() };
    let r = install::install(&dirs, &g, &bundle, &opts, &mut p).unwrap();
    say!("installed {} ({:?}), {} files, platform {:?}", r.runtime.version, r.runtime.components, r.files.len(), r.platform);
    assert_eq!(r.platform, Some(Platform::Windows));
    assert!(dir.join("dinput8.dll").is_file() && dir.join("kiln/kiln-core.dll").is_file() && dir.join("kiln/mods/kiln-admin").is_dir());
    let g = game::inspect(&dir).unwrap(); // the receipt's installId now
    assert_eq!(g.id, r.install_id);

    // 3. a windows server record (a record an earlier, aborted run left for this folder goes first)
    for s in instance::list().unwrap().into_iter().filter(|s| s.source != "steam" && s.dir.as_deref() == Some(dir.as_path())) {
        instance::remove(&s.id, false).unwrap();
    }
    let inst = instance::import(&dir, "Windows Test Ü").unwrap();
    say!("server {} runner {:?} query port {} dir {}", inst.id, inst.runner, inst.query_port, dir.display());
    assert_eq!(inst.runner, ServerRunner::Windows);
    let id = inst.id.clone();
    let cleanup = || {
        let _ = instance::remove(&id, false);
    };

    // 4. start: returns when the log says the world runs
    let t0 = Instant::now();
    let st = run::start(&inst, &mut p);
    let st = match st {
        Ok(s) => s,
        Err(e) => {
            cleanup();
            say!("start failed: {e} ({:?})", e.detail);
            panic!("start failed: {e}");
        }
    };
    say!("started in {:.0} s: {:?} pid {:?} kiln {:?}", t0.elapsed().as_secs_f64(), st.state, st.pid, st.kiln);
    assert_eq!(st.state, ServerState::Running);
    let pid = st.pid.expect("pid of the running server");
    assert!(kiln_manage::proc::alive(pid));
    // refused while it runs
    assert!(run::start(&inst, &mut p).is_err(), "a second start must be refused");
    assert_eq!(install::install(&dirs, &g, &bundle, &opts, &mut p).unwrap_err().code, kiln_manage::ErrorCode::GameRunning);

    // 5. Kiln booted in it: boot.json with the Windows pid and Windows paths
    let boot = st.boot.clone().expect("boot.json of the running server");
    say!("boot: pid {} side {:?} hooks {} policy {:?} exe {:?} errors {:?}", boot.pid, boot.side, boot.hooks, boot.policy, boot.exe, boot.errors);
    say!("boot paths: {:?}", boot.paths);
    assert_eq!(boot.pid, pid, "boot.json pid = the server's pid");
    assert!(kiln_manage::launch::boot_is_current(&g, &boot));

    // 6. the admin connection over KAP/1 (TCP, endpoint.json + admin-token), then the A2S query
    let ep = st.endpoint.clone().expect("endpoint.json");
    say!("endpoint: {}:{} pid {} kiln {:?}", ep.host, ep.port, ep.pid, ep.kiln);
    let deadline = Instant::now() + Duration::from_secs(60);
    let client = loop {
        match AdminClient::connect_dir(&dir, &inst.id, Transport::Tcp) {
            Ok(c) => break c,
            Err(e) if Instant::now() < deadline => {
                say!("  admin not ready: {e}");
                std::thread::sleep(Duration::from_secs(2));
            }
            Err(e) => {
                cleanup();
                panic!("admin connection failed: {e}");
            }
        }
    };
    let info = client.call("admin_info", Default::default()).unwrap();
    say!("admin_info: kiln {} protocol {} transports {} endpoint {}", info["kiln"], info["protocol"], info["transports"], info["endpoint"]);
    assert_eq!(info["protocol"], "kap/1");
    let players = client.call("players", Default::default()).unwrap();
    say!("players: {players}");
    let q = kiln_manage::server::query::query(&format!("127.0.0.1:{}", inst.query_port), Duration::from_secs(2));
    say!("A2S: {q:?}");
    let status = run::status(&inst).unwrap();
    say!("status: {:?} pid {:?} uptime {:?} players {:?}", status.state, status.pid, status.uptime_s, status.players);
    drop(client);

    // 7. stop: the world is saved, the process ends
    let save_dir = run::save_dir(&inst).unwrap();
    let newest = || kiln_manage::server::game::newest_file(&save_dir).map(|(_, t, _)| t);
    let before = newest();
    let t1 = Instant::now();
    let stopped = run::stop(&inst, &mut p);
    say!("stop: {:?} after {:.1} s", stopped.as_ref().map(|s| s.state), t1.elapsed().as_secs_f64());
    let stopped = stopped.unwrap();
    assert_eq!(stopped.state, ServerState::Stopped);
    assert!(!kiln_manage::proc::alive(pid), "pid {pid} still runs");
    assert!(newest() > before, "the world was saved during the stop ({} )", save_dir.display());

    // 8. uninstall gives the folder back
    install::uninstall(&dirs, &g, &mut p).unwrap();
    assert!(!dir.join("kiln/.install.json").exists() && !dir.join("dinput8.dll").exists());

    // 9. the vanilla server (no Kiln, no admin): start and a graceful stop through its console
    if std::env::var_os("KILN_WINSRV_VANILLA").is_some_and(|v| v == "1") {
        let st = match run::start(&inst, &mut p) {
            Ok(s) => s,
            Err(e) => {
                cleanup();
                panic!("vanilla start failed: {e}");
            }
        };
        let vpid = st.pid.expect("pid");
        assert!(st.boot.is_none() && st.endpoint.is_none(), "no Kiln in the vanilla server");
        say!("vanilla: running, pid {vpid}, uptime {:?} s", st.uptime_s);
        let before = newest();
        let stopped = match run::stop(&inst, &mut p) {
            Ok(s) => s,
            Err(e) => {
                cleanup();
                say!("vanilla stop failed (pid {vpid} left running): {e}");
                panic!("vanilla stop failed: {e}");
            }
        };
        assert_eq!(stopped.state, ServerState::Stopped);
        assert!(!kiln_manage::proc::alive(vpid));
        assert!(newest() > before, "the vanilla server saved on Ctrl-C");
    }
    cleanup();
    say!("windows_server_lifecycle: PASS");
}
