//! AdminClient / AdminHub tests against an in-process KAP/1 mock (and a fake file-bridge server). The real
//! Kiln.Admin is tested in the lab by tools/nettest/kaptest.py; `lab_tcp_equals_bridge` (ignored) runs this client
//! against a lab: `KILN_LAB=~/srv/enshrouded/labs/<lab> cargo test -p kiln-manage --all-features -- --ignored lab_`.

use super::client::{AdminClient, AdminEvent, Transport};
use super::hub::{AdminHub, HubEvent};
use super::protocol;
use crate::ErrorCode;
use serde_json::{json, Value};
use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

const TOKEN: [u8; 32] = [7u8; 32];

fn temp_dir(tag: &str) -> PathBuf {
    let d = std::env::temp_dir().join(format!("kiln-admin-test-{tag}-{}-{}", std::process::id(), crate::hash::hex(&crate::util::random_bytes::<4>().unwrap())));
    std::fs::create_dir_all(&d).unwrap();
    d
}

fn send(s: &mut TcpStream, v: &Value) -> std::io::Result<()> {
    let b = serde_json::to_vec(v).unwrap();
    s.write_all(&(b.len() as u32).to_le_bytes())?;
    s.write_all(&b)
}

fn recv(s: &mut TcpStream) -> Option<Value> {
    let mut h = [0u8; 4];
    s.read_exact(&mut h).ok()?;
    let mut b = vec![0u8; u32::from_le_bytes(h) as usize];
    s.read_exact(&mut b).ok()?;
    serde_json::from_slice(&b).ok()
}

/// A KAP/1 server: ops ping/echo/fail/slow, topic state (a push every 100 ms), drops every connection when `drop`
/// is set, answers pings, sends one ping itself.
struct Mock {
    port: u16,
    drop: Arc<AtomicBool>,
    accepted: Arc<AtomicUsize>,
    pongs: Arc<AtomicUsize>,
    bad_ready_mac: bool,
}

impl Mock {
    fn start(token: [u8; 32], bad_ready_mac: bool) -> Mock {
        let l = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = l.local_addr().unwrap().port();
        let drop = Arc::new(AtomicBool::new(false));
        let accepted = Arc::new(AtomicUsize::new(0));
        let pongs = Arc::new(AtomicUsize::new(0));
        let (d, a, p) = (drop.clone(), accepted.clone(), pongs.clone());
        std::thread::spawn(move || {
            for s in l.incoming() {
                let Ok(s) = s else { continue };
                a.fetch_add(1, Ordering::SeqCst);
                let (d, p) = (d.clone(), p.clone());
                std::thread::spawn(move || serve(s, token, d, p, bad_ready_mac));
            }
        });
        Mock { port, drop, accepted, pongs, bad_ready_mac }
    }

    /// endpoint.json + admin-token in `<dir>/kiln/data/kiln-admin/`, like Kiln.Admin writes them.
    fn write_endpoint(&self, dir: &Path, token: &[u8; 32]) {
        let a = super::discover::admin_dir(dir);
        std::fs::create_dir_all(&a).unwrap();
        std::fs::write(a.join("admin-token"), format!("{}\n", crate::hash::hex(token))).unwrap();
        std::fs::write(
            a.join("endpoint.json"),
            serde_json::to_vec(&json!({"format": 1, "protocol": "kap/1", "host": "127.0.0.1", "port": self.port, "pid": 1,
                "started": "2026-11-04T12:00:00Z", "server": {"id": "srv-test", "name": "Test"}, "token": "admin-token"}))
            .unwrap(),
        )
        .unwrap();
        let _ = self.bad_ready_mac;
    }
}

fn serve(mut s: TcpStream, token: [u8; 32], drop: Arc<AtomicBool>, pongs: Arc<AtomicUsize>, bad_ready_mac: bool) {
    let nonce = crate::hash::hex(&[0x11u8; 32]);
    send(&mut s, &json!({"t": "hello", "proto": "kap/1", "nonce": nonce, "kiln": "0.2.0-dev", "server": {"id": "srv-test", "name": "Test"}, "auth": ["hmac-sha256"]})).unwrap();
    let Some(auth) = recv(&mut s) else { return };
    let cnonce = auth["cnonce"].as_str().unwrap_or("").to_string();
    if auth["mac"].as_str() != Some(protocol::mac(&token, "kap1-auth", &nonce, &cnonce).as_str()) {
        let _ = send(&mut s, &json!({"t": "error", "code": "auth_failed", "message": "wrong mac"}));
        return;
    }
    let smac = if bad_ready_mac { "00".repeat(32) } else { protocol::mac(&token, "kap1-ready", &nonce, &cnonce) };
    send(&mut s, &json!({"t": "ready", "session": "s1", "mac": smac, "ops": ["ping", "echo", "fail", "slow"], "topics": ["state", "log"],
        "limits": {"maxFrame": 16777216, "maxClientFrame": 1048576, "inflight": 64, "pingS": 15, "idleS": 45}})).unwrap();
    let w = Arc::new(Mutex::new(s.try_clone().unwrap()));
    let subscribed = Arc::new(AtomicBool::new(false));
    {
        let (w, sub, drop) = (w.clone(), subscribed.clone(), drop.clone());
        std::thread::spawn(move || {
            let mut seq = 0u64;
            let _ = send(&mut w.lock().unwrap(), &json!({"t": "ping", "id": 1, "ts": 5}));
            loop {
                std::thread::sleep(Duration::from_millis(100));
                if drop.load(Ordering::SeqCst) {
                    let _ = w.lock().unwrap().shutdown(std::net::Shutdown::Both);
                    return;
                }
                if sub.load(Ordering::SeqCst) {
                    seq += 1;
                    if send(&mut w.lock().unwrap(), &json!({"t": "push", "topic": "state", "seq": seq, "data": {"clock": seq}})).is_err() {
                        return;
                    }
                }
            }
        });
    }
    while let Some(f) = recv(&mut s) {
        let id = f["id"].clone();
        let reply = match f["t"].as_str() {
            Some("req") => match f["op"].as_str().unwrap_or("") {
                "ping" => json!({"t": "res", "id": id, "ok": true, "op": "ping", "ms": 0, "data": {"pong": true}}),
                "echo" => json!({"t": "res", "id": id, "ok": true, "op": "echo", "ms": 0, "data": f["args"]}),
                "fail" => json!({"t": "res", "id": id, "ok": false, "op": "fail", "ms": 0, "error": "entity must be an entity id", "code": "op_failed"}),
                "slow" => {
                    let w = w.clone();
                    std::thread::spawn(move || {
                        std::thread::sleep(Duration::from_millis(300));
                        let _ = send(&mut w.lock().unwrap(), &json!({"t": "res", "id": id, "ok": true, "op": "slow", "ms": 300, "data": 1}));
                    });
                    continue;
                }
                op => json!({"t": "res", "id": id, "ok": false, "op": op, "error": format!("unknown op {op}"), "code": "unknown_op"}),
            },
            Some("sub") => {
                subscribed.store(true, Ordering::SeqCst);
                json!({"t": "res", "id": id, "ok": true, "op": "sub"})
            }
            Some("unsub") => {
                subscribed.store(false, Ordering::SeqCst);
                json!({"t": "res", "id": id, "ok": true, "op": "unsub"})
            }
            Some("pong") => {
                pongs.fetch_add(1, Ordering::SeqCst);
                continue;
            }
            Some("bye") => return,
            _ => json!({"t": "error", "code": "bad_frame", "message": "?"}),
        };
        if send(&mut w.lock().unwrap(), &reply).is_err() {
            return;
        }
    }
}

fn wait_for(mut f: impl FnMut() -> bool, secs: u64) -> bool {
    let end = Instant::now() + Duration::from_secs(secs);
    while Instant::now() < end {
        if f() {
            return true;
        }
        std::thread::sleep(Duration::from_millis(20));
    }
    f()
}

#[test]
fn tcp_handshake_calls_pushes_and_pings() {
    let m = Mock::start(TOKEN, false);
    let dir = temp_dir("tcp");
    m.write_endpoint(&dir, &TOKEN);
    let c = AdminClient::connect_dir(&dir, "srv-test", Transport::Auto).unwrap();
    assert_eq!(c.session().transport, Transport::Tcp);
    assert_eq!(c.session().ops, ["ping", "echo", "fail", "slow"]);
    assert_eq!(c.session().server.id.as_deref(), Some("srv-test"));
    assert_eq!(c.call("ping", Default::default()).unwrap(), json!({"pong": true}));
    let mut args = serde_json::Map::new();
    args.insert("x".into(), json!(5));
    assert_eq!(c.call("echo", args).unwrap(), json!({"x": 5}));
    let e = c.call("fail", Default::default()).unwrap_err();
    assert_eq!(e.code, ErrorCode::OpFailed);
    assert_eq!(e.message, "entity must be an entity id");
    assert_eq!(e.detail.as_ref().unwrap()["kapCode"], "op_failed");
    let e = c.call("nope", Default::default()).unwrap_err();
    assert_eq!(e.detail.as_ref().unwrap()["kapCode"], "unknown_op");
    // concurrent calls on one client: answers matched by id
    let c = Arc::new(c);
    let slow = { let c = c.clone(); std::thread::spawn(move || c.call("slow", Default::default())) };
    assert_eq!(c.call("ping", Default::default()).unwrap(), json!({"pong": true}));
    assert_eq!(slow.join().unwrap().unwrap(), json!(1));
    c.subscribe("state").unwrap();
    let mut seqs = vec![];
    while seqs.len() < 3 {
        if let Some(AdminEvent::Push { topic, seq, .. }) = c.next_event(Duration::from_secs(2)).unwrap() {
            assert_eq!(topic, "state");
            seqs.push(seq);
        }
    }
    assert!(seqs.windows(2).all(|w| w[1] == w[0] + 1));
    assert!(wait_for(|| m.pongs.load(Ordering::SeqCst) >= 1, 3), "the client answers the server's ping");
    c.shutdown();
    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn wrong_token_and_wrong_server_mac_are_refused() {
    let m = Mock::start(TOKEN, false);
    let dir = temp_dir("auth");
    m.write_endpoint(&dir, &[9u8; 32]);
    let e = AdminClient::connect_dir(&dir, "srv-test", Transport::Tcp).err().unwrap();
    assert_eq!(e.code, ErrorCode::AuthFailed, "{e}");
    // Auto does not fall back to the file bridge on an auth failure
    let e = AdminClient::connect_dir(&dir, "srv-test", Transport::Auto).err().unwrap();
    assert_eq!(e.code, ErrorCode::AuthFailed);
    let impostor = Mock::start(TOKEN, true);
    impostor.write_endpoint(&dir, &TOKEN);
    let e = AdminClient::connect_dir(&dir, "srv-test", Transport::Tcp).err().unwrap();
    assert_eq!(e.code, ErrorCode::AuthFailed, "{e}");
    let _ = std::fs::remove_dir_all(dir);
}

#[test]
fn hub_reconnects_and_resubscribes() {
    let m = Mock::start(TOKEN, false);
    let dir = temp_dir("hub");
    m.write_endpoint(&dir, &TOKEN);
    let events: Arc<Mutex<Vec<HubEvent>>> = Arc::new(Mutex::new(vec![]));
    let ev = events.clone();
    let mut hub = AdminHub::new(move |e| ev.lock().unwrap().push(e));
    hub.backoff = (Duration::from_millis(100), Duration::from_millis(400));
    let d = dir.clone();
    let s = hub.connect("srv-test", Box::new(move || AdminClient::connect_dir(&d, "srv-test", Transport::Auto))).unwrap();
    let d2 = dir.clone();
    let again = hub.connect("srv-test", Box::new(move || AdminClient::connect_dir(&d2, "srv-test", Transport::Auto))).unwrap();
    assert_eq!(s.session, again.session, "one session per server");
    hub.subscribe(&s.session, "state").unwrap();
    assert!(wait_for(|| events.lock().unwrap().iter().any(|e| matches!(e.event, AdminEvent::Push { .. })), 3));
    // the server drops every connection once
    m.drop.store(true, Ordering::SeqCst);
    assert!(wait_for(|| events.lock().unwrap().iter().any(|e| matches!(e.event, AdminEvent::Closed { retry: true, .. })), 3));
    m.drop.store(false, Ordering::SeqCst);
    assert!(wait_for(|| events.lock().unwrap().iter().filter(|e| matches!(e.event, AdminEvent::Ready { .. })).count() >= 2, 5));
    let n = events.lock().unwrap().len();
    assert!(wait_for(|| events.lock().unwrap()[n..].iter().any(|e| matches!(e.event, AdminEvent::Push { .. })), 3), "re-subscribed after the reconnect");
    assert!(m.accepted.load(Ordering::SeqCst) >= 2);
    assert_eq!(hub.call(&s.session, "ping", Default::default()).unwrap(), json!({"pong": true}));
    assert!(events.lock().unwrap().iter().all(|e| e.session == s.session && e.server_id == "srv-test"));
    hub.disconnect(&s.session).unwrap();
    assert!(matches!(events.lock().unwrap().last().unwrap().event, AdminEvent::Closed { retry: false, .. }));
    assert_eq!(hub.call(&s.session, "ping", Default::default()).unwrap_err().code, ErrorCode::NotFound);
    let _ = std::fs::remove_dir_all(dir);
}

/// A fake Kiln.Admin on the file bridge: answers requests like Bridge.cs, refreshes state.json.
fn fake_bridge_server(dir: PathBuf, stop: Arc<AtomicBool>) {
    let (req, res) = (dir.join("requests"), dir.join("results"));
    std::fs::create_dir_all(&req).unwrap();
    std::fs::create_dir_all(&res).unwrap();
    let mut n = 0u64;
    while !stop.load(Ordering::SeqCst) {
        n += 1;
        if n % 5 == 0 {
            std::fs::write(dir.join("state.json"), format!("{{\"clock\":{n},\"ready\":true}}")).unwrap();
        }
        for e in std::fs::read_dir(&req).unwrap().flatten() {
            let name = e.file_name().to_string_lossy().to_string();
            if name.starts_with('.') || !name.ends_with(".json") {
                continue;
            }
            let Ok(body) = std::fs::read(e.path()) else { continue };
            let _ = std::fs::remove_file(e.path());
            let r: Value = serde_json::from_slice(&body).unwrap();
            let out = match r["op"].as_str() {
                Some("status") => json!({"op": "status", "data": {"kiln": "0.2.0-dev", "ops": ["ping", "status"]}, "ok": true, "ms": 0}),
                Some("ping") => json!({"op": "ping", "data": {"pong": true}, "ok": true, "ms": 0}),
                Some(op) => json!({"op": op, "ok": false, "error": format!("unknown op {op}")}),
                None => json!({"ok": false}),
            };
            std::fs::write(res.join(&name), serde_json::to_vec(&out).unwrap()).unwrap();
        }
        std::thread::sleep(Duration::from_millis(20));
    }
}

#[test]
fn auto_falls_back_to_the_file_bridge() {
    let dir = temp_dir("file");
    let bridge = dir.join("shroudforge/exports/emberwatch");
    let stop = Arc::new(AtomicBool::new(false));
    let (b, st) = (bridge.clone(), stop.clone());
    let server = std::thread::spawn(move || fake_bridge_server(b, st));
    assert!(wait_for(|| bridge.join("state.json").exists(), 3));
    let c = AdminClient::connect_dir(&dir, "srv-file", Transport::Auto).unwrap();
    assert_eq!(c.session().transport, Transport::File);
    assert_eq!(c.session().ops, ["ping", "status"]);
    assert_eq!(c.call("ping", Default::default()).unwrap(), json!({"pong": true}));
    let e = c.call("nope", Default::default()).unwrap_err();
    assert_eq!((e.code, e.message.as_str()), (ErrorCode::OpFailed, "unknown op nope"));
    c.subscribe("state").unwrap();
    let mut got = 0;
    let end = Instant::now() + Duration::from_secs(3);
    while got < 2 && Instant::now() < end {
        if let Some(AdminEvent::Push { topic, data, .. }) = c.next_event(Duration::from_millis(500)).unwrap() {
            assert_eq!(topic, "state");
            assert_eq!(data["ready"], true);
            got += 1;
        }
    }
    assert_eq!(got, 2);
    c.shutdown();
    stop.store(true, Ordering::SeqCst);
    server.join().unwrap();
    // nothing serves the folder now: Auto gives admin_unavailable once state.json is old (simulated: removed)
    std::fs::remove_file(bridge.join("state.json")).unwrap();
    let e = AdminClient::connect_dir(&dir, "srv-file", Transport::Auto).err().unwrap();
    assert_eq!(e.code, ErrorCode::AdminUnavailable);
    let _ = std::fs::remove_dir_all(dir);
}

/// Against a running lab (Kiln.Admin): every read-only op answers the same over TCP and the file bridge.
#[test]
#[ignore]
fn lab_tcp_equals_bridge() {
    let lab = PathBuf::from(std::env::var("KILN_LAB").expect("KILN_LAB=<lab dir>"));
    let tcp = AdminClient::connect_dir(&lab, "lab", Transport::Tcp).unwrap();
    let file = AdminClient::connect_dir(&lab, "lab", Transport::File).unwrap();
    assert_eq!(tcp.session().ops, file.session().ops);
    for op in ["players", "mods", "zones_list", "permissions_get", "config_list", "commands", "network_status", "native_status", "backup_list"] {
        if !tcp.session().ops.iter().any(|o| o == op) {
            continue;
        }
        let a = tcp.call(op, Default::default());
        let b = file.call(op, Default::default());
        assert_eq!(a.is_ok(), b.is_ok(), "{op}");
        println!("{op}: ok {}", a.is_ok());
    }
    tcp.subscribe("state").unwrap();
    let t0 = Instant::now();
    let mut n = 0;
    while n < 3 && t0.elapsed() < Duration::from_secs(6) {
        if let Some(AdminEvent::Push { .. }) = tcp.next_event(Duration::from_secs(2)).unwrap() {
            n += 1;
        }
    }
    assert!(n >= 3, "state pushes");
    println!("3 pushes in {:?}", t0.elapsed());
}

/// A real tunnel through the user's ssh config: `KILN_SSH_HOST=<alias> cargo test ... -- --ignored lab_ssh`. Forwards
/// to the host's own sshd (port 22) and reads its banner through the tunnel; nothing is changed on the host.
#[test]
#[ignore]
fn lab_ssh_tunnel() {
    let host = std::env::var("KILN_SSH_HOST").expect("KILN_SSH_HOST=<ssh alias>");
    let remote = crate::server::remote::RemoteSpec { host, port: None, dir: "/tmp".into(), instance: None };
    assert_eq!(crate::server::remote::run(&remote, &["echo", "it's ok"]).unwrap().trim(), "it's ok");
    let t = super::tunnel::SshTunnel::open(&remote, 22).unwrap();
    let mut s = TcpStream::connect(("127.0.0.1", t.local_port)).unwrap();
    s.set_read_timeout(Some(Duration::from_secs(5))).unwrap();
    let mut banner = [0u8; 8];
    s.read_exact(&mut banner).unwrap();
    assert!(banner.starts_with(b"SSH-"), "{:?}", String::from_utf8_lossy(&banner));
    let port = t.local_port;
    t.close().unwrap();
    std::thread::sleep(Duration::from_millis(300));
    assert!(!super::discover::probe("127.0.0.1", port, Duration::from_millis(300)), "tunnel closed");
    println!("tunnel ok via local port {port}");
}
