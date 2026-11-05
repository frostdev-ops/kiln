//! Windows lab test of the windowless start; runs under Wine too:
//! `cargo xwin test -p kiln-manage --all-features --target x86_64-pc-windows-msvc --no-run`, then
//! `KILN_TEST_SERVER_DIR='Z:\...\EnshroudedServer' wine <kiln_manage-*.exe> --ignored --nocapture lab_windows`
//! (a scratch prefix; on Xvfb, the window list shows whether a console window opened).
//! Starts the exe with CREATE_NO_WINDOW and its output in the capture file, waits for the world, checks the console,
//! stops it with Ctrl-C into its console.

use super::console::{ConsoleFollower, ConsoleSource};
use super::instance::{InstanceKind, ServerInstance, ServerRunner};
use super::{run, ServerState};
use std::path::PathBuf;

#[test]
#[ignore]
fn lab_windows_windowless() {
    let dir = PathBuf::from(std::env::var("KILN_TEST_SERVER_DIR").expect("KILN_TEST_SERVER_DIR"));
    let inst = ServerInstance {
        id: "win-lab".into(),
        name: "win lab".into(),
        kind: InstanceKind::Local,
        dir: Some(dir.clone()),
        port: 26252,
        query_port: 26252,
        wine: None,
        policy: "any".into(),
        source: "launcher".into(),
        remote: None,
        runner: ServerRunner::Windows,
        prefix: None,
        backup_dir: None,
        kilnctl: None,
        stop_timeout_s: 60,
    };
    let mut cb = |f: Option<f32>, m: &str| println!("[{}] {m}", f.map(|f| format!("{:3.0}%", f * 100.0)).unwrap_or_else(|| "  ? ".into()));
    assert!(run::pid(&inst).is_none(), "stop it first");
    let t0 = std::time::Instant::now();
    let st = run::start(&inst, &mut cb).expect("start");
    println!("started in {:.1} s: {:?} pid {:?}", t0.elapsed().as_secs_f64(), st.state, st.pid);
    assert_eq!(st.state, ServerState::Running);
    let (snap, _) = ConsoleFollower::open(&inst, 5000);
    println!("console {:?} {:?}: {} lines; first {:?}", snap.source, snap.path, snap.lines.len(), snap.lines.first());
    assert_eq!(snap.source, ConsoleSource::Capture);
    assert!(snap.lines.iter().any(|l| l.text.contains(super::game::READY)), "the server's stdout is in the capture file");
    let pid = st.pid.expect("pid");
    let t1 = std::time::Instant::now();
    let st = run::stop(&inst, &mut cb).expect("stop");
    println!("stopped in {:.1} s: {:?}", t1.elapsed().as_secs_f64(), st.state);
    assert_eq!(st.state, ServerState::Stopped);
    assert!(!crate::proc::alive(pid));
    let text = std::fs::read_to_string(super::console::capture_path(&inst).unwrap()).unwrap();
    let tail: Vec<&str> = text.lines().rev().take(400).collect();
    assert!(tail.iter().any(|l| l.contains("exit: Ctrl_C") || l.contains("Close Log file")), "a graceful shutdown in the output");
    println!("shutdown line: {:?}", tail.iter().find(|l| l.contains("exit: Ctrl_C")));
}
