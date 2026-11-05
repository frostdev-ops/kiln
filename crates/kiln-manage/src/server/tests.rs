//! Lab tests of the server layer (ignored by default: they run SteamCMD and Wine).
//!
//! `KILN_TEST_SERVER_DIR=~/srv/enshrouded/labs/<name> KILN_TEST_SERVER_PORT=26011 [KILN_TEST_REFLINK_FROM=<install>]
//!  XDG_CONFIG_HOME=<scratch> XDG_STATE_HOME=<scratch> KILN_DATA_DIR=<scratch>
//!  cargo test -p kiln-manage --all-features -- --ignored --nocapture lab_instance`

use super::instance::{self, ServerRunner, ServerSpec};
use super::{run, ServerState};
use std::path::PathBuf;
use std::time::Duration;

fn print_progress(f: Option<f32>, m: &str) {
    println!("[{}] {m}", f.map(|f| format!("{:3.0}%", f * 100.0)).unwrap_or_else(|| "  ? ".into()));
}

#[test]
#[ignore]
fn lab_instance_lifecycle() {
    let dir = PathBuf::from(std::env::var("KILN_TEST_SERVER_DIR").expect("KILN_TEST_SERVER_DIR"));
    let port: u16 = std::env::var("KILN_TEST_SERVER_PORT").expect("KILN_TEST_SERVER_PORT").parse().unwrap();
    let id = dir.file_name().unwrap().to_string_lossy().to_string();
    for v in ["XDG_CONFIG_HOME", "XDG_STATE_HOME", "KILN_DATA_DIR"] {
        let p = std::env::var(v).unwrap_or_else(|_| panic!("{v} must point at a scratch folder"));
        assert!(!p.contains("/.config") && !p.contains("/.local"), "{v}={p}: use a scratch folder, not the user's");
    }
    let mut cb = print_progress;
    let inst = match instance::get(&id) {
        Ok(i) => i,
        Err(_) => instance::create(
            &ServerSpec {
                id: id.clone(),
                name: format!("Kiln launcher test {id}"),
                dir: dir.clone(),
                port,
                reflink_from: std::env::var_os("KILN_TEST_REFLINK_FROM").map(PathBuf::from),
                policy: Some("any".into()),
                wine: None,
                skip_steamcmd: std::env::var_os("KILN_TEST_SKIP_STEAMCMD").is_some(),
            },
            &mut cb,
        )
        .expect("create"),
    };
    println!("instance: {}", serde_json::to_string(&inst).unwrap());
    assert_eq!(inst.runner, ServerRunner::Kilnctl);
    assert_eq!(inst.query_port, port);
    assert!(instance::list().unwrap().iter().any(|s| s.id == id));
    let st = run::status(&inst).unwrap();
    assert_eq!(st.state, ServerState::Stopped, "{st:?}");
    let t0 = std::time::Instant::now();
    let st = run::start(&inst, &mut cb).expect("start");
    println!("started in {:.1} s: {st:?}", t0.elapsed().as_secs_f64());
    assert_eq!(st.state, ServerState::Running);
    let pid = st.pid.expect("pid");
    let q = super::query::query(&format!("127.0.0.1:{port}"), Duration::from_secs(2)).expect("A2S");
    println!("A2S: {q:?}");
    assert_eq!(q.players, 0);
    assert!(run::start(&inst, &mut cb).is_err(), "a second start is refused");
    let settings = super::settings::read(&inst).unwrap();
    assert!(settings.running && settings.values["queryPort"] == port);
    assert!(super::settings::write(&inst, &serde_json::json!({"slotCount": 3})).is_err(), "no edits while running");
    let st = run::stop(&inst, &mut cb).expect("stop");
    println!("stopped: {st:?}");
    assert_eq!(st.state, ServerState::Stopped);
    assert!(!super::procs::alive(pid));
    let s = super::settings::write(&inst, &serde_json::json!({"slotCount": 3})).expect("edit while stopped");
    assert_eq!(s.values["slotCount"], 3);
    let b = super::backup::create(&inst, &mut cb).expect("backup");
    println!("backup: {b:?}");
    assert!(super::backup::list(&inst).unwrap().iter().any(|x| x.name == b.name));
}

/// Windowless start + console, for any local runner:
/// `KILN_TEST_SERVER_ID=<id> XDG_CONFIG_HOME=<scratch> XDG_STATE_HOME=<scratch> KILN_DATA_DIR=<scratch>
///  [KILN_STEAM_ROOTS=<fake Steam root>] <test binary> --ignored --nocapture lab_console`
/// Starts the server, checks the console shows the run from its start marker with game-log levels, follows it
/// through the stop. Window checks are outside (the server reaches no display: run::headless).
#[test]
#[ignore]
fn lab_console_windowless() {
    use super::console::{ConsoleFollower, ConsoleLevel, ConsoleSource, ConsoleUpdate};
    let id = std::env::var("KILN_TEST_SERVER_ID").expect("KILN_TEST_SERVER_ID");
    for v in ["XDG_CONFIG_HOME", "XDG_STATE_HOME", "KILN_DATA_DIR"] {
        let p = std::env::var(v).unwrap_or_else(|_| panic!("{v} must point at a scratch folder"));
        assert!(!p.contains("/.config") && !p.contains("/.local"), "{v}={p}: use a scratch folder, not the user's");
    }
    let mut cb = print_progress;
    let inst = instance::get(&id).expect("instance");
    println!("instance: {} runner {:?} prefix {:?}", inst.id, inst.runner, inst.prefix);
    assert_eq!(run::status(&inst).unwrap().state, ServerState::Stopped, "stop it first");
    let st = run::start(&inst, &mut cb).expect("start");
    assert_eq!(st.state, ServerState::Running);
    let (snap, mut f) = ConsoleFollower::open(&inst, 5000);
    println!("console: {:?} {:?}, {} lines, truncated {}", snap.source, snap.path, snap.lines.len(), snap.truncated);
    for l in snap.lines.iter().take(4) {
        println!("  {:?} {}", l.level, l.text);
    }
    assert_eq!(snap.source, ConsoleSource::Capture);
    assert!(snap.lines[0].text.starts_with("---- kiln-launcher start "), "{:?}", snap.lines[0]);
    assert_eq!(snap.lines[0].level, ConsoleLevel::Meta);
    assert!(snap.lines.iter().any(|l| l.text.contains(super::game::READY)), "the ready line is in the console");
    let warned: Vec<_> = snap.lines.iter().filter(|l| l.level == ConsoleLevel::Warn || l.level == ConsoleLevel::Error).collect();
    println!("{} warn/error lines, e.g. {:?}", warned.len(), warned.first());
    assert!(!warned.is_empty(), "game-log levels reach the captured lines");
    let stopped = run::stop(&inst, &mut cb).expect("stop");
    assert_eq!(stopped.state, ServerState::Stopped);
    let mut after = Vec::new();
    for _ in 0..8 {
        if let ConsoleUpdate::Lines(l) = f.poll() {
            after.extend(l);
        }
        std::thread::sleep(Duration::from_millis(500));
    }
    println!("{} lines followed through the stop, last: {:?}", after.len(), after.last());
    assert!(after.iter().any(|l| l.text.contains("Close Log file")), "the follower saw the shutdown");
}
