//! (0.2.0-dev.31.19) "Open web panel" at the panel's address on the internet (`launcher-endpoint.json` `public`,
//! Kilnwarden 0.1.4): the file's new fields (and older launchers' files), the check before a login goes there, the
//! hosted open (a one-use bootstrap address of its own, no gateway), the fallback to this computer when the check
//! fails, "Open on this computer instead", and a remote server over ssh. The per-server setting "Always open the web
//! panel on this computer" (`web-panel-settings.json`): kept across restarts, and while it is on the public address is
//! never checked or used, for local and remote servers alike.

use super::*;
use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream};
use std::path::Path;
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::{Duration, Instant};

fn temp(tag: &str) -> PathBuf {
    let d = std::env::temp_dir().join(format!("kiln-webpanel-pub-{tag}-{}-{}", std::process::id(), crate::hash::hex(&crate::util::random_bytes::<6>().unwrap())));
    std::fs::create_dir_all(&d).unwrap();
    d
}

fn server(id: &str, dir: &Path) -> ServerInstance {
    serde_json::from_value(serde_json::json!({
        "id": id, "name": id, "kind": "local", "dir": dir, "port": 15636, "queryPort": 15637, "wine": null,
        "policy": "verified-only", "source": "launcher", "remote": null
    }))
    .unwrap()
}

fn http_get(addr: SocketAddr, path: &str) -> (u16, String) {
    let mut s = TcpStream::connect(addr).unwrap();
    s.set_read_timeout(Some(Duration::from_secs(5))).unwrap();
    write!(s, "GET {path} HTTP/1.1\r\nHost: {addr}\r\n\r\n").unwrap();
    let mut out = String::new();
    let _ = s.read_to_string(&mut out);
    let status = out.split(' ').nth(1).and_then(|c| c.parse().ok()).unwrap_or(0);
    (status, out.split_once("\r\n\r\n").map(|(_, b)| b.to_string()).unwrap_or_default())
}

/// A fresh self-signed certificate and key (openssl at test time; RSA and a v3 extension, as tests.rs explains).
fn cert() -> Option<(rustls::pki_types::CertificateDer<'static>, rustls::pki_types::PrivateKeyDer<'static>, String)> {
    use rustls::pki_types::pem::PemObject;
    let dir = temp("cert");
    let ok = std::process::Command::new("openssl")
        .args(["req", "-x509", "-newkey", "rsa:2048", "-nodes", "-subj", "/CN=panel", "-days", "1", "-addext", "basicConstraints=critical,CA:FALSE"])
        .arg("-keyout")
        .arg(dir.join("k.pem"))
        .arg("-out")
        .arg(dir.join("c.pem"))
        .output()
        .ok()?
        .status
        .success();
    if !ok {
        return None;
    }
    let c = rustls::pki_types::CertificateDer::from_pem_file(dir.join("c.pem")).unwrap();
    let k = rustls::pki_types::PrivateKeyDer::from_pem_file(dir.join("k.pem")).unwrap();
    let _ = std::fs::remove_dir_all(&dir);
    let pin = crate::hash::sha256_hex(c.as_ref());
    Some((c, k, pin))
}

/// What the stand-in for the panel at its public address does with a connection.
#[derive(Clone)]
enum Answer {
    /// Completes TLS and answers one request with this raw HTTP response.
    Http(String),
    /// Accepts the TCP connection and never says anything.
    Stall,
}

fn json_answer(status: &str, body: &str) -> Answer {
    Answer::Http(format!("HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {}\r\n\r\n{body}", body.len()))
}

const READY: &str = r#"{"ownerExists":true,"signedIn":false,"launcherLogin":true,"providers":["local"]}"#;

/// The panel at its address on the internet (TLS on 127.0.0.1, reached as `localhost`): its address, its pin, how many
/// connections it took (`accepts`, any contact at all) and how many requests it saw (each one's first line in `seen`).
struct PublicPanel {
    addr: SocketAddr,
    pin: String,
    accepts: Arc<AtomicUsize>,
    requests: Arc<AtomicUsize>,
    seen: Arc<std::sync::Mutex<Vec<String>>>,
}

impl PublicPanel {
    fn start(answer: Answer) -> Option<PublicPanel> {
        let (c, k, pin) = cert()?;
        let provider = Arc::new(rustls::crypto::ring::default_provider());
        let cfg = Arc::new(rustls::ServerConfig::builder_with_provider(provider).with_safe_default_protocol_versions().unwrap().with_no_client_auth().with_single_cert(vec![c], k).unwrap());
        let l = TcpListener::bind("127.0.0.1:0").unwrap();
        let addr = l.local_addr().unwrap();
        let accepts = Arc::new(AtomicUsize::new(0));
        let requests = Arc::new(AtomicUsize::new(0));
        let seen = Arc::new(std::sync::Mutex::new(Vec::new()));
        let (a2, r2, s2) = (accepts.clone(), requests.clone(), seen.clone());
        std::thread::spawn(move || {
            for s in l.incoming().flatten() {
                a2.fetch_add(1, Ordering::SeqCst);
                let (cfg, answer, requests, seen) = (cfg.clone(), answer.clone(), r2.clone(), s2.clone());
                std::thread::spawn(move || {
                    let mut s = s;
                    let Answer::Http(response) = answer else {
                        std::thread::sleep(Duration::from_secs(10));
                        return;
                    };
                    let Ok(mut conn) = rustls::ServerConnection::new(cfg) else { return };
                    let mut tls = rustls::Stream::new(&mut conn, &mut s);
                    let mut req = Vec::new();
                    let mut b = [0u8; 1024];
                    while !req.windows(4).any(|w| w == b"\r\n\r\n") {
                        match tls.read(&mut b) {
                            Ok(0) | Err(_) => return,
                            Ok(n) => req.extend_from_slice(&b[..n]),
                        }
                    }
                    requests.fetch_add(1, Ordering::SeqCst);
                    seen.lock().unwrap().push(String::from_utf8_lossy(&req).split("\r\n").take(2).collect::<Vec<_>>().join(" | "));
                    let _ = tls.write_all(response.as_bytes());
                    let _ = tls.flush();
                    tls.conn.send_close_notify();
                    let _ = tls.flush();
                });
            }
        });
        Some(PublicPanel { addr, pin, accepts, requests, seen })
    }

    fn address(&self) -> endpoint::PublicAddress {
        endpoint::PublicAddress::parse(&format!("https://localhost:{}/", self.addr.port()), &self.pin).unwrap()
    }
}

/// A port nothing listens on.
fn dead_port() -> u16 {
    TcpListener::bind("127.0.0.1:0").unwrap().local_addr().unwrap().port()
}

// ------------------------------------------------------------------------------------------------- the file

#[test]
fn the_files_public_address_is_read_and_a_bad_one_is_left_out_without_losing_the_local_one() {
    let pin = ["AB"; 32].join(":");
    let base = |extra: &str| format!(r#"{{"format":1,"host":"127.0.0.1","port":15737,"sha256":"{pin}"{extra}}}"#);
    // an older Kilnwarden's file: no public address
    let ep = endpoint::Endpoint::parse(base("").as_bytes()).unwrap();
    assert_eq!(ep.public, None);
    // Kilnwarden 0.1.4: the hosted name and its certificate, normalized
    let ep = endpoint::Endpoint::parse(base(&format!(r#","public":"https://Alpha.Kiln.Nexus/","publicSha256":"{}""#, ["CD"; 32].join(":"))).as_bytes()).unwrap();
    let p = ep.public.unwrap();
    assert_eq!((p.url.as_str(), p.host.as_str(), p.port, p.sha256.as_str()), ("https://alpha.kiln.nexus/", "alpha.kiln.nexus", 443, "cd".repeat(32).as_str()));
    assert_eq!(ep.sha256.as_deref(), Some("ab".repeat(32).as_str()), "this machine's pin is unchanged");
    // an own address with its port; 443 written out is the same address
    let own = endpoint::PublicAddress::parse("https://panel.example.com:8443/", &"ef".repeat(32)).unwrap();
    assert_eq!((own.url.as_str(), own.port), ("https://panel.example.com:8443/", 8443));
    assert_eq!(endpoint::PublicAddress::parse("https://panel.example.com:443/", &"ef".repeat(32)).unwrap().url, "https://panel.example.com/");
    // anything else is left out, and the local address still opens
    let sha = "ef".repeat(32);
    for (url, sha) in [
        ("http://alpha.kiln.nexus/", sha.as_str()),
        ("https://alpha.kiln.nexus", sha.as_str()),
        ("https://alpha.kiln.nexus/launcher", sha.as_str()),
        ("https://alpha.kiln.nexus/?x=1", sha.as_str()),
        ("https://alpha.kiln.nexus/#x", sha.as_str()),
        ("https://user@alpha.kiln.nexus/", sha.as_str()),
        ("https://203.0.113.7/", sha.as_str()),
        ("https://[2001:db8::1]/", sha.as_str()),
        ("https://alpha.kiln.nexus:0/", sha.as_str()),
        ("https://alpha.kiln.nexus:+443/", sha.as_str()),
        ("https://alpha.kiln.nexus:99999/", sha.as_str()),
        ("https://-bad.kiln.nexus/", sha.as_str()),
        ("https://alpha..kiln.nexus/", sha.as_str()),
        ("https://alpha.kiln.nexus/", "not a fingerprint"),
    ] {
        assert_eq!(endpoint::PublicAddress::parse(url, sha), None, "{url} {sha}");
        let ep = endpoint::Endpoint::parse(base(&format!(r#","public":{},"publicSha256":"{sha}""#, serde_json::to_string(url).unwrap())).as_bytes()).unwrap();
        assert_eq!((ep.public, ep.port), (None, 15737), "{url}");
    }
    // only one of the two, or not strings: left out
    for extra in [r#","public":"https://alpha.kiln.nexus/""#.to_string(), format!(r#","publicSha256":"{sha}""#), format!(r#","public":7,"publicSha256":"{sha}""#)] {
        assert_eq!(endpoint::Endpoint::parse(base(&extra).as_bytes()).unwrap().public, None, "{extra}");
    }
}

// ------------------------------------------------------------------------------------------------- the check

#[test]
fn the_check_wants_this_panels_certificate_an_owner_and_the_launchers_login() {
    let Some(ok) = PublicPanel::start(json_answer("200 OK", READY)) else {
        eprintln!("skipped: no openssl command");
        return;
    };
    assert_eq!(public::check(&ok.address(), Duration::from_secs(5)), Ok(()));
    let seen = ok.seen.lock().unwrap().clone();
    assert_eq!(seen.len(), 1);
    assert!(seen[0].starts_with("GET /api/auth/status HTTP/1.1 | Host: localhost:"), "{seen:?}");
    // another certificate at the name (another server has it now, or something in between): refused, no request sent
    let mut other = ok.address();
    other.sha256 = "00".repeat(32);
    assert_eq!(public::check(&other, Duration::from_secs(5)), Err(public::Fallback::Refused));
    assert_eq!(ok.requests.load(Ordering::SeqCst), 1, "nothing was asked of a server with another certificate");
    // it answers, but can't sign the owner in: refused
    for answer in [
        json_answer("200 OK", r#"{"ownerExists":false,"launcherLogin":true}"#),
        json_answer("200 OK", r#"{"ownerExists":true,"launcherLogin":false}"#),
        json_answer("200 OK", "not json"),
        json_answer("503 Service Unavailable", READY),
        json_answer("404 Not Found", r#"{"error":{"code":"not_found"}}"#),
    ] {
        let p = PublicPanel::start(answer).unwrap();
        assert_eq!(public::check(&p.address(), Duration::from_secs(5)), Err(public::Fallback::Refused));
    }
    // a chunked answer is read whole
    let body = READY;
    let chunked = Answer::Http(format!("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n{:x}\r\n{}\r\n{:x}\r\n{}\r\n0\r\n\r\n", 10, &body[..10], body.len() - 10, &body[10..]));
    assert_eq!(public::check(&PublicPanel::start(chunked).unwrap().address(), Duration::from_secs(5)), Ok(()));
}

#[test]
fn no_answer_in_time_is_unreachable() {
    let Some(stall) = PublicPanel::start(Answer::Stall) else {
        eprintln!("skipped: no openssl command");
        return;
    };
    let t = Instant::now();
    assert_eq!(public::check(&stall.address(), Duration::from_millis(600)), Err(public::Fallback::Unreachable));
    assert!(t.elapsed() < Duration::from_secs(3), "the check keeps to its time: {:?}", t.elapsed());
    let dead = endpoint::PublicAddress::parse(&format!("https://localhost:{}/", dead_port()), &"ab".repeat(32)).unwrap();
    assert_eq!(public::check(&dead, Duration::from_secs(2)), Err(public::Fallback::Unreachable));
    let nowhere = endpoint::PublicAddress::parse("https://no-such-panel.invalid/", &"ab".repeat(32)).unwrap();
    assert_eq!(public::check(&nowhere, Duration::from_secs(3)), Err(public::Fallback::Unreachable));
}

/// (review) A peer that announces a TLS record and then sends it a byte at a time, each byte well within any one read's
/// timeout: the check still ends at its deadline. rustls's handshake reads many times in one call, so a socket timeout
/// set once before it would start again with every byte, and the click (and the web panels' lock) would wait as long
/// as the peer likes.
#[test]
fn a_peer_that_drips_its_answer_still_gets_only_the_checks_time() {
    let l = TcpListener::bind("127.0.0.1:0").unwrap();
    let port = l.local_addr().unwrap().port();
    std::thread::spawn(move || {
        for s in l.incoming().flatten() {
            std::thread::spawn(move || {
                let mut s = s;
                // a 16 KiB handshake record, then one byte every 100 ms for 5 s
                if s.write_all(&[0x16, 0x03, 0x03, 0x40, 0x00]).is_err() {
                    return;
                }
                for _ in 0..50 {
                    std::thread::sleep(Duration::from_millis(100));
                    if s.write_all(&[0x02]).is_err() {
                        return;
                    }
                }
            });
        }
    });
    let slow = endpoint::PublicAddress::parse(&format!("https://localhost:{port}/"), &"ab".repeat(32)).unwrap();
    let t = Instant::now();
    assert_eq!(public::check(&slow, Duration::from_millis(800)), Err(public::Fallback::Unreachable));
    assert!(t.elapsed() < Duration::from_millis(2000), "the check keeps to its time: {:?}", t.elapsed());
}

/// (review) A name with two addresses where the first swallows connections without a word (a broken IPv6 route, with
/// the relay's AAAA record first in the answer): the next address still gets its turn within the check's time, as a
/// browser's would, instead of the first one taking all of it and the panel always opening on this computer.
#[cfg(target_os = "linux")]
#[test]
fn an_address_that_swallows_connections_leaves_time_for_the_next_one() {
    use std::os::fd::AsRawFd;
    // a listener whose queue is full drops further connection attempts without an answer (Linux)
    let hole = TcpListener::bind("127.0.0.1:0").unwrap();
    assert_eq!(unsafe { libc::listen(hole.as_raw_fd(), 0) }, 0);
    let hole_addr = hole.local_addr().unwrap();
    let _queued: Vec<_> = (0..2).filter_map(|_| TcpStream::connect_timeout(&hole_addr, Duration::from_millis(300)).ok()).collect();
    assert!(TcpStream::connect_timeout(&hole_addr, Duration::from_millis(300)).is_err(), "the stand-in for a dead route answers nothing");
    let good = TcpListener::bind("127.0.0.1:0").unwrap();
    let good_addr = good.local_addr().unwrap();
    let t = Instant::now();
    let s = public::connect(&[hole_addr, good_addr], Instant::now() + Duration::from_secs(4));
    assert_eq!(s.and_then(|s| s.peer_addr().ok()), Some(good_addr), "the second address, after {:?}", t.elapsed());
    assert!(t.elapsed() < Duration::from_millis(1600), "{:?}", t.elapsed());
    // a single address keeps the whole time
    let t = Instant::now();
    assert!(public::connect(&[hole_addr], Instant::now() + Duration::from_millis(1500)).is_none());
    assert!(t.elapsed() >= Duration::from_millis(1400), "{:?}", t.elapsed());
}

// ------------------------------------------------------------------------------------------------- opening

/// The panel on this machine (TLS handshake only, the gateway's pin check) and its file with `public`.
fn local_panel() -> Option<(SocketAddr, String)> {
    let (c, k, pin) = cert()?;
    let provider = Arc::new(rustls::crypto::ring::default_provider());
    let cfg = Arc::new(rustls::ServerConfig::builder_with_provider(provider).with_safe_default_protocol_versions().unwrap().with_no_client_auth().with_single_cert(vec![c], k).unwrap());
    let l = TcpListener::bind("127.0.0.1:0").unwrap();
    let addr = l.local_addr().unwrap();
    std::thread::spawn(move || {
        for s in l.incoming().flatten() {
            let cfg = cfg.clone();
            std::thread::spawn(move || {
                let mut s = s;
                let Ok(mut conn) = rustls::ServerConnection::new(cfg) else { return };
                while conn.is_handshaking() {
                    if conn.complete_io(&mut s).is_err() {
                        return;
                    }
                }
                let _ = conn.complete_io(&mut s);
            });
        }
    });
    Some((addr, pin))
}

/// Kiln.Admin's files for the KAP mock in `data/kiln-admin`, and Kilnwarden's launcher-endpoint.json in
/// `data/kilnwarden` (`public` when given).
fn write_files(data: &Path, kap_port: u16, token: &[u8; 32], local: (SocketAddr, &str), public: Option<(&str, &str)>) {
    let ka = data.join("kiln-admin");
    std::fs::create_dir_all(&ka).unwrap();
    std::fs::write(ka.join("admin-token"), format!("{}\n", crate::hash::hex(token))).unwrap();
    std::fs::write(
        ka.join("endpoint.json"),
        serde_json::to_vec(&serde_json::json!({"format": 1, "protocol": "kap/1", "host": "127.0.0.1", "port": kap_port, "pid": 1,
            "started": "2026-11-26T12:00:00Z", "server": {"id": "srv-test", "name": "Test"}, "token": "admin-token"})).unwrap(),
    )
    .unwrap();
    let kw = data.join("kilnwarden");
    std::fs::create_dir_all(&kw).unwrap();
    let mut ep = serde_json::json!({ "format": 1, "host": "127.0.0.1", "port": local.0.port(), "sha256": local.1 });
    if let Some((url, sha)) = public {
        ep["public"] = url.into();
        ep["publicSha256"] = sha.into();
    }
    std::fs::write(kw.join(endpoint::FILE), serde_json::to_vec(&ep).unwrap()).unwrap();
}

/// The bootstrap page's target (what the browser is sent on to), fetched once.
fn bootstrap_target(url: &str) -> (u16, String) {
    let (host, path) = url.strip_prefix("http://").unwrap().split_once('/').unwrap();
    http_get(host.parse().unwrap(), &format!("/{path}"))
}

#[test]
fn a_working_public_address_opens_there_with_the_login_and_without_a_gateway() {
    let (Some((local, local_pin)), Some(public)) = (local_panel(), PublicPanel::start(json_answer("200 OK", READY))) else {
        eprintln!("skipped: no openssl command");
        return;
    };
    let dir = temp("hosted");
    let token = [9u8; 32];
    let kap = crate::admin::tests::Mock::start(token, false);
    let url = format!("https://localhost:{}/", public.addr.port());
    write_files(&dir.join("kiln/data"), kap.port, &token, (local, &local_pin), Some((&url, &public.pin)));
    let s = server("hosted", &dir);
    let mut panels = WebPanels::new(dir.join(origins::FILE));
    let opened = panels.open(&s).unwrap();
    let info = &opened.info;
    assert_eq!((info.via, info.public.as_deref(), info.fallback), (Via::Hosted, Some(url.as_str()), None));
    assert_eq!((info.origin.as_str(), info.fingerprint.as_deref()), (url.trim_end_matches('/'), Some(public.pin.as_str())));
    assert!(opened.url.starts_with("http://127.0.0.1:") && !opened.url.contains("secret"), "a one-use local address, no secret: {}", opened.url);
    assert!(panels.list().is_empty(), "no gateway for the hosted address");
    // the page sends the browser to the public address with the login, once
    let (status, body) = bootstrap_target(&opened.url);
    assert_eq!(status, 200);
    assert!(body.contains(&format!("{url}launcher#secret={}", "5e".repeat(32))), "{body}");
    assert!(body.contains("location.replace("));
    let (status, _) = bootstrap_target(&opened.url);
    assert_ne!(status, 200, "the page works once");
    // "Open on this computer instead": the gateway, whatever the file says; the check is not even run
    let before = public.requests.load(Ordering::SeqCst);
    let here = panels.open_via(&s, Route::Local).unwrap();
    assert_eq!((here.info.via, here.info.public.clone(), here.info.fallback), (Via::Local, None, None));
    assert!(here.info.origin.starts_with("https://127."), "{}", here.info.origin);
    let (_, body) = bootstrap_target(&here.url);
    assert!(body.contains(&format!("{}/launcher#secret=", here.info.origin)), "{body}");
    assert_eq!(public.requests.load(Ordering::SeqCst), before);
    assert_eq!(panels.list().len(), 1);
    panels.close_all();
    let _ = std::fs::remove_dir_all(&dir);
}

#[test]
fn a_public_address_that_fails_its_check_opens_on_this_computer_and_says_why() {
    let (Some((local, local_pin)), Some(refusing)) = (local_panel(), PublicPanel::start(json_answer("200 OK", r#"{"ownerExists":false,"launcherLogin":true}"#))) else {
        eprintln!("skipped: no openssl command");
        return;
    };
    let token = [8u8; 32];
    let kap = crate::admin::tests::Mock::start(token, false);
    let cases = [
        (format!("https://localhost:{}/", refusing.addr.port()), refusing.pin.clone(), public::Fallback::Refused),
        // the right port but another certificate: the name answers for someone else
        (format!("https://localhost:{}/", refusing.addr.port()), "00".repeat(32), public::Fallback::Refused),
        (format!("https://localhost:{}/", dead_port()), refusing.pin.clone(), public::Fallback::Unreachable),
    ];
    for (i, (url, pin, why)) in cases.into_iter().enumerate() {
        let dir = temp(&format!("fallback{i}"));
        write_files(&dir.join("kiln/data"), kap.port, &token, (local, &local_pin), Some((&url, &pin)));
        let mut panels = WebPanels::new(dir.join(origins::FILE));
        panels.set_public_timeout(Duration::from_secs(2));
        let opened = panels.open(&server("fallback", &dir)).unwrap();
        assert_eq!((opened.info.via, opened.info.public.clone(), opened.info.fallback), (Via::Local, None, Some(why)), "{url}");
        let (_, body) = bootstrap_target(&opened.url);
        assert!(body.contains(&format!("{}/launcher#secret=", opened.info.origin)) && !body.contains("localhost"), "{body}");
        panels.close_all();
        let _ = std::fs::remove_dir_all(&dir);
    }
    // an older Kilnwarden's file (no public address): as before, no fallback to tell
    let dir = temp("nopublic");
    write_files(&dir.join("kiln/data"), kap.port, &token, (local, &local_pin), None);
    let mut panels = WebPanels::new(dir.join(origins::FILE));
    let opened = panels.open(&server("nopublic", &dir)).unwrap();
    assert_eq!((opened.info.via, opened.info.fallback), (Via::Local, None));
    panels.close_all();
    let _ = std::fs::remove_dir_all(&dir);
}

#[test]
fn a_one_use_address_of_its_own_expires() {
    let url = gateway::offer_once("https://alpha.kiln.nexus/launcher#secret=x".into(), Duration::from_millis(300)).unwrap();
    std::thread::sleep(Duration::from_millis(500));
    let host: SocketAddr = url.strip_prefix("http://").unwrap().split_once('/').unwrap().0.parse().unwrap();
    // the listener is gone (or, if a last connection raced it, says the link expired)
    let gone = TcpStream::connect_timeout(&host, Duration::from_secs(1)).is_err() || bootstrap_target(&url).0 == 404;
    assert!(gone);
    // a wrong handle never takes the page; the right one still does, once
    let url = gateway::offer_once("https://alpha.kiln.nexus/launcher#secret=y".into(), Duration::from_secs(10)).unwrap();
    let wrong = format!("{}/k/{}", url.rsplit_once("/k/").unwrap().0, "0".repeat(64));
    assert_eq!(bootstrap_target(&wrong).0, 404);
    let (status, body) = bootstrap_target(&url);
    assert_eq!(status, 200);
    assert!(body.contains("https://alpha.kiln.nexus/launcher#secret=y"));
}

/// A remote server over ssh: the file comes over ssh, the check goes straight to the public address, the login is
/// minted through the ssh tunnel.
#[cfg(unix)]
#[test]
fn a_remote_server_over_ssh_opens_at_its_public_address_too() {
    let Some(ssh) = super::review_tests::fake_ssh() else {
        eprintln!("skipped: no python3 for the ssh stand-in");
        return;
    };
    let (Some((local, local_pin)), Some(public)) = (local_panel(), PublicPanel::start(json_answer("200 OK", READY))) else {
        eprintln!("skipped: no openssl command");
        return;
    };
    let _env = super::review_tests::SshEnv::set(&ssh, 0);
    let dir = temp("remote");
    let token = [7u8; 32];
    let kap = crate::admin::tests::Mock::start(token, false);
    let url = format!("https://localhost:{}/", public.addr.port());
    write_files(&dir.join("kiln/data"), kap.port, &token, (local, &local_pin), Some((&url, &public.pin)));
    let mut s = server("remote", &dir);
    s.dir = None;
    s.remote = Some(crate::server::remote::RemoteSpec { host: "lab-box".into(), port: None, dir: dir.display().to_string(), instance: None });
    let mut panels = WebPanels::new(temp("remote-origins").join(origins::FILE));
    let opened = panels.open(&s).unwrap();
    assert_eq!((opened.info.via, opened.info.public.as_deref()), (Via::Hosted, Some(url.as_str())));
    let (status, body) = bootstrap_target(&opened.url);
    assert_eq!(status, 200);
    assert!(body.contains(&format!("{url}launcher#secret={}", "5e".repeat(32))), "{body}");
    // and on this computer instead: through ssh, as before
    let here = panels.open_via(&s, Route::Local).unwrap();
    assert_eq!(here.info.via, Via::Ssh);
    panels.close_all();
    let _ = std::fs::remove_dir_all(&dir);
}

// ------------------------------------------------------------------------------------------------- always on this computer

#[test]
fn the_always_on_this_computer_setting_is_kept_per_server_and_survives_a_restart() {
    let dir = temp("settings");
    let registry = dir.join(origins::FILE);
    let file = dir.join(settings::FILE);
    let on = settings::WebPanelSettings { always_local: true };
    let panels = WebPanels::new(registry.clone());
    // off by default: the hosted address when it works, as before
    assert_eq!(panels.settings("lab"), settings::WebPanelSettings::default());
    assert!(!panels.settings("lab").always_local);
    assert_eq!(panels.set_settings("lab", on).unwrap(), on);
    assert_eq!(panels.settings("lab"), on);
    assert!(!panels.settings("other").always_local, "per server");
    // in the launcher's data folder, beside the origins
    let saved: serde_json::Value = serde_json::from_slice(&std::fs::read(&file).unwrap()).unwrap();
    assert_eq!(saved, serde_json::json!({"format": 1, "servers": {"lab": {"alwaysLocal": true}}}));
    // a restart (a new launcher process: new WebPanels on the same folder) keeps it, for a remote server's id too
    panels.set_settings("remote-box", on).unwrap();
    drop(panels);
    let again = WebPanels::new(registry.clone());
    assert!(again.settings("lab").always_local && again.settings("remote-box").always_local);
    // off again: the server's entry goes, the others stay
    assert_eq!(again.set_settings("lab", settings::WebPanelSettings::default()).unwrap(), settings::WebPanelSettings::default());
    let saved: serde_json::Value = serde_json::from_slice(&std::fs::read(&file).unwrap()).unwrap();
    assert_eq!(saved, serde_json::json!({"format": 1, "servers": {"remote-box": {"alwaysLocal": true}}}));
    assert!(!WebPanels::new(registry.clone()).settings("lab").always_local);
    // a file that can't be read counts as on for every server (the panel then opens on this computer), and a change
    // leaves it as it is (a_change_never_replaces_an_unreadable_settings_file_...); a missing file is all off
    std::fs::write(&file, b"{ not json").unwrap();
    assert!(settings::get(&file, "lab").always_local && settings::get(&file, "anyone").always_local);
    std::fs::write(&file, br#"{"format":2,"servers":{}}"#).unwrap();
    assert!(settings::get(&file, "lab").always_local, "a newer format is not guessed at");
    assert!(settings::set(&file, "lab", on).is_err());
    assert!(settings::get(&file, "lab").always_local && settings::get(&file, "anyone").always_local);
    std::fs::remove_file(&file).unwrap();
    assert!(!settings::get(&file, "lab").always_local);
    let _ = std::fs::remove_dir_all(&dir);
}

/// A file that is there but can't be read counts as on for every server, so a change must never replace it: that would
/// turn every other server's "Always open the web panel on this computer" off (as far as the launcher could tell they
/// were all on), and drop a newer launcher's file. The change is refused, says which file, and leaves it as it was.
#[test]
fn a_change_never_replaces_an_unreadable_settings_file_so_no_other_server_loses_the_setting() {
    let dir = temp("settings-unreadable");
    let file = dir.join(settings::FILE);
    let panels = WebPanels::new(dir.join(origins::FILE));
    let on = settings::WebPanelSettings { always_local: true };
    let off = settings::WebPanelSettings::default();
    for bad in [&b"{ not json"[..], br#"{"format":2,"servers":{"lab":{"alwaysLocal":false}}}"#, b""] {
        std::fs::write(&file, bad).unwrap();
        for s in [off, on] {
            let e = panels.set_settings("lab", s).expect_err("a change over a file that can't be read is refused");
            assert!(e.message.contains(settings::FILE), "{}", e.message);
            assert_eq!(std::fs::read(&file).unwrap(), bad, "the file stays as it was");
            assert!(panels.settings("lab").always_local && panels.settings("other").always_local, "still on for every server");
        }
    }
    // once it is fixed or deleted, changes work again
    std::fs::remove_file(&file).unwrap();
    assert_eq!(panels.set_settings("lab", on).unwrap(), on);
    assert!(panels.settings("lab").always_local && !panels.settings("other").always_local);
    let _ = std::fs::remove_dir_all(&dir);
}

#[test]
fn with_always_on_this_computer_open_web_panel_never_checks_or_uses_the_public_address() {
    let (Some((local, local_pin)), Some(public)) = (local_panel(), PublicPanel::start(json_answer("200 OK", READY))) else {
        eprintln!("skipped: no openssl command");
        return;
    };
    let dir = temp("always-local");
    let token = [6u8; 32];
    let kap = crate::admin::tests::Mock::start(token, false);
    let url = format!("https://localhost:{}/", public.addr.port());
    write_files(&dir.join("kiln/data"), kap.port, &token, (local, &local_pin), Some((&url, &public.pin)));
    let s = server("always", &dir);
    let mut panels = WebPanels::new(dir.join(origins::FILE));
    panels.set_settings("always", settings::WebPanelSettings { always_local: true }).unwrap();
    // "auto" (what Open web panel asks for): this computer, and the public address is not even contacted
    let opened = panels.open(&s).unwrap();
    assert_eq!((opened.info.via, opened.info.public.clone(), opened.info.fallback), (Via::Local, None, None));
    assert!(opened.info.origin.starts_with("https://127."), "{}", opened.info.origin);
    let (_, body) = bootstrap_target(&opened.url);
    assert!(body.contains(&format!("{}/launcher#secret=", opened.info.origin)) && !body.contains("localhost"), "{body}");
    std::thread::sleep(Duration::from_millis(200));
    assert_eq!(public.accepts.load(Ordering::SeqCst), 0, "no check: not even a connection to the public address");
    // after a restart too
    panels.close_all();
    let mut panels = WebPanels::new(dir.join(origins::FILE));
    assert_eq!(panels.open(&s).unwrap().info.via, Via::Local);
    assert_eq!(public.accepts.load(Ordering::SeqCst), 0);
    // off: the hosted address again (the check runs and passes)
    panels.set_settings("always", settings::WebPanelSettings::default()).unwrap();
    let opened = panels.open(&s).unwrap();
    assert_eq!((opened.info.via, opened.info.public.as_deref()), (Via::Hosted, Some(url.as_str())));
    assert!(public.accepts.load(Ordering::SeqCst) >= 1 && public.requests.load(Ordering::SeqCst) == 1);
    // the setting is the server's: another server with the same file still opens at its public address
    panels.set_settings("always", settings::WebPanelSettings { always_local: true }).unwrap();
    assert_eq!(panels.open(&server("neighbour", &dir)).unwrap().info.via, Via::Hosted);
    panels.close_all();
    let _ = std::fs::remove_dir_all(&dir);
}

/// A remote server over ssh with the setting on: the panel opens through the ssh tunnel on this computer, and the public
/// address is never contacted from here.
#[cfg(unix)]
#[test]
fn a_remote_server_with_always_on_this_computer_opens_through_ssh_without_a_check() {
    let Some(ssh) = super::review_tests::fake_ssh() else {
        eprintln!("skipped: no python3 for the ssh stand-in");
        return;
    };
    let (Some((local, local_pin)), Some(public)) = (local_panel(), PublicPanel::start(json_answer("200 OK", READY))) else {
        eprintln!("skipped: no openssl command");
        return;
    };
    let _env = super::review_tests::SshEnv::set(&ssh, 0);
    let dir = temp("remote-always");
    let token = [5u8; 32];
    let kap = crate::admin::tests::Mock::start(token, false);
    let url = format!("https://localhost:{}/", public.addr.port());
    write_files(&dir.join("kiln/data"), kap.port, &token, (local, &local_pin), Some((&url, &public.pin)));
    let mut s = server("remote-always", &dir);
    s.dir = None;
    s.remote = Some(crate::server::remote::RemoteSpec { host: "lab-box".into(), port: None, dir: dir.display().to_string(), instance: None });
    let origins_dir = temp("remote-always-origins");
    let mut panels = WebPanels::new(origins_dir.join(origins::FILE));
    panels.set_settings("remote-always", settings::WebPanelSettings { always_local: true }).unwrap();
    let opened = panels.open(&s).unwrap();
    assert_eq!((opened.info.via, opened.info.public.clone(), opened.info.fallback), (Via::Ssh, None, None));
    std::thread::sleep(Duration::from_millis(200));
    assert_eq!(public.accepts.load(Ordering::SeqCst), 0);
    panels.close_all();
    let _ = std::fs::remove_dir_all(&dir);
    let _ = std::fs::remove_dir_all(&origins_dir);
}
