//! The two sources against local HTTP servers, with the real updater plugin on a mock app: source order, fallback
//! on a missing manifest, a dead host, a failed download, a bad signature, a wrong signed version and a wrong sha256.
//! testdata/: `payload.bin` (starts like an ELF file, as an AppImage does), its signature `payload.bin.sig` (signed
//! for version 0.2.0-dev.2) and `test.pub`, the public half of a throwaway key used only here.

use super::feed::{Channel, Source};
use super::*;
use std::collections::HashMap;
use std::io::{Read, Write};
use std::net::TcpListener;

const PAYLOAD: &[u8] = include_bytes!("testdata/payload.bin");
const SIG: &str = include_str!("testdata/payload.bin.sig");
const PUBKEY: &str = include_str!("testdata/test.pub");

/// A tiny HTTP/1.1 server: fixed answers by path, a hit counter per path.
struct Server {
    port: u16,
    hits: Arc<Mutex<HashMap<String, usize>>>,
}

impl Server {
    fn start(routes: Vec<(String, u16, Vec<u8>)>) -> Server {
        Server::serve(TcpListener::bind("127.0.0.1:0").unwrap(), routes)
    }

    fn serve(l: TcpListener, routes: Vec<(String, u16, Vec<u8>)>) -> Server {
        let port = l.local_addr().unwrap().port();
        let routes: HashMap<String, (u16, Vec<u8>)> = routes.into_iter().map(|(p, s, b)| (p, (s, b))).collect();
        let hits = Arc::new(Mutex::new(HashMap::new()));
        let h = hits.clone();
        std::thread::spawn(move || {
            for conn in l.incoming() {
                let Ok(mut c) = conn else { continue };
                let mut buf = Vec::new();
                let mut chunk = [0u8; 2048];
                while !buf.windows(4).any(|w| w == b"\r\n\r\n") {
                    match c.read(&mut chunk) {
                        Ok(0) | Err(_) => break,
                        Ok(n) => buf.extend_from_slice(&chunk[..n]),
                    }
                }
                let req = String::from_utf8_lossy(&buf);
                let path = req.split_whitespace().nth(1).unwrap_or("/").to_string();
                *h.lock().unwrap().entry(path.clone()).or_insert(0) += 1;
                let (status, body) = routes.get(&path).cloned().unwrap_or((404, b"not found".to_vec()));
                let head = format!("HTTP/1.1 {status} X\r\nContent-Length: {}\r\nConnection: close\r\n\r\n", body.len());
                let _ = c.write_all(head.as_bytes());
                let _ = c.write_all(&body);
            }
        });
        Server { port, hits }
    }

    fn url(&self, path: &str) -> String {
        format!("http://127.0.0.1:{}{path}", self.port)
    }

    fn hits(&self, path: &str) -> usize {
        self.hits.lock().unwrap().get(path).copied().unwrap_or(0)
    }

    fn total_hits(&self) -> usize {
        self.hits.lock().unwrap().values().sum()
    }

    fn source(&self, name: &str) -> Source {
        Source { name: name.into(), stable: self.url("/stable/latest.json"), dev: self.url("/dev/latest.json") }
    }
}

/// A port nothing listens on.
fn dead_source(name: &str) -> Source {
    let port = TcpListener::bind("127.0.0.1:0").unwrap().local_addr().unwrap().port();
    Source { name: name.into(), stable: format!("http://127.0.0.1:{port}/stable/latest.json"), dev: format!("http://127.0.0.1:{port}/dev/latest.json") }
}

fn manifest(version: &str, file_url: &str, sha256: Option<&str>) -> Vec<u8> {
    let mut entry = serde_json::json!({ "url": file_url, "signature": SIG.trim() });
    if let Some(s) = sha256 {
        entry["sha256"] = s.into();
    }
    serde_json::to_vec(&serde_json::json!({
        "version": version,
        "notes": "Test release.",
        "pub_date": "2026-11-06T12:00:00Z",
        "platforms": { "linux-x86_64": entry.clone(), "windows-x86_64": entry },
        "kiln": { "format": 1, "channel": "dev" },
    }))
    .unwrap()
}

/// A feed that offers `version` with `body` as the file (and `sha256` listed, if given).
fn feed(version: &str, body: &[u8], sha256: Option<&str>) -> Server {
    // the manifest names the server's own port: bind first and keep the listener (dropping it and binding again let
    // a parallel test take the port in between: "port … stayed busy")
    let l = TcpListener::bind("127.0.0.1:0").unwrap();
    let port = l.local_addr().unwrap().port();
    let file = format!("http://127.0.0.1:{port}/files/kiln.AppImage");
    let m = manifest(version, &file, sha256);
    Server::serve(l, vec![("/dev/latest.json".into(), 200, m.clone()), ("/stable/latest.json".into(), 200, m), ("/files/kiln.AppImage".into(), 200, body.to_vec())])
}

fn tampered() -> Vec<u8> {
    let mut b = PAYLOAD.to_vec();
    let last = b.len() - 2;
    b[last] ^= 0x20;
    b
}

/// A mock app at version `current` with the updater plugin and the test key.
fn app(current: &str) -> tauri::App<tauri::test::MockRuntime> {
    let mut ctx = tauri::test::mock_context(tauri::test::noop_assets());
    ctx.config_mut().plugins.0.insert(
        "updater".into(),
        serde_json::json!({ "pubkey": PUBKEY.trim(), "requireSignedVersion": true, "dangerousInsecureTransportProtocol": true }),
    );
    ctx.package_info_mut().version = current.parse().unwrap();
    tauri::test::mock_builder().plugin(tauri_plugin_updater::Builder::new().build()).build(ctx).unwrap()
}

fn run<T>(f: impl std::future::Future<Output = T>) -> T {
    tauri::async_runtime::block_on(f)
}

fn steps(f: &[Failure]) -> Vec<(&str, Step)> {
    f.iter().map(|f| (f.source.as_str(), f.step)).collect()
}

#[test]
fn try_sources_stops_at_the_first_success() {
    let srcs = vec![dead_source("a"), dead_source("b"), dead_source("c")];
    let tried = Arc::new(Mutex::new(Vec::new()));
    let t = tried.clone();
    let (ok, failures) = run(try_sources(&srcs, move |s| {
        let t = t.clone();
        async move {
            t.lock().unwrap().push(s.name.clone());
            if s.name == "a" { Err(Failure { source: s.name, step: Step::Manifest, message: "down".into() }) } else { Ok(s.name.len()) }
        }
    }));
    assert_eq!(ok, Some(("b".to_string(), 1)));
    assert_eq!(*tried.lock().unwrap(), ["a", "b"]);
    assert_eq!(steps(&failures), [("a", Step::Manifest)]);
}

#[test]
fn check_asks_frostdev_first() {
    let (f, g) = (feed("0.2.0-dev.2", PAYLOAD, None), feed("0.2.0-dev.2", PAYLOAD, None));
    let app = app("0.2.0-dev.1");
    let (answer, failures) = run(check_sources(app.handle(), &[f.source("frostdev.io"), g.source("GitHub")], Channel::Dev));
    let (source, update) = answer.unwrap();
    assert_eq!(source, "frostdev.io");
    assert_eq!(update.unwrap().version, "0.2.0-dev.2");
    assert!(failures.is_empty());
    assert_eq!(f.hits("/dev/latest.json"), 1);
    assert_eq!(g.total_hits(), 0, "GitHub is not asked when frostdev.io answers");
}

#[test]
fn check_falls_back_to_github_when_frostdev_is_down_or_has_no_manifest() {
    let g = feed("0.2.0-dev.2", PAYLOAD, None);
    let app = app("0.2.0-dev.1");
    let (answer, failures) = run(check_sources(app.handle(), &[dead_source("frostdev.io"), g.source("GitHub")], Channel::Dev));
    assert_eq!(answer.unwrap().0, "GitHub");
    assert_eq!(steps(&failures), [("frostdev.io", Step::Manifest)]);
    assert!(failures[0].message.starts_with("not reachable"), "{}", failures[0].message);

    let empty = Server::start(vec![]);
    let (answer, failures) = run(check_sources(app.handle(), &[empty.source("frostdev.io"), g.source("GitHub")], Channel::Dev));
    assert_eq!(answer.unwrap().0, "GitHub");
    assert_eq!(steps(&failures), [("frostdev.io", Step::Manifest)]);
    assert_eq!(failures[0].message, "no update file there");
    assert_eq!(empty.hits("/dev/latest.json"), 1);
}

#[test]
fn up_to_date_is_an_answer() {
    let (f, g) = (feed("0.2.0-dev.2", PAYLOAD, None), feed("0.2.0-dev.3", PAYLOAD, None));
    let app = app("0.2.0-dev.2");
    let (answer, failures) = run(check_sources(app.handle(), &[f.source("frostdev.io"), g.source("GitHub")], Channel::Dev));
    let (source, update) = answer.unwrap();
    assert_eq!(source, "frostdev.io");
    assert!(update.is_none());
    assert!(failures.is_empty());
    assert_eq!(g.total_hits(), 0);
}

#[test]
fn stable_channel_reads_the_stable_manifest_and_skips_pre_releases() {
    let f = feed("0.2.0-dev.2", PAYLOAD, None);
    let app = app("0.1.0");
    let (answer, _) = run(check_sources(app.handle(), &[f.source("frostdev.io")], Channel::Stable));
    assert!(answer.unwrap().1.is_none(), "a dev build is no update for stable");
    assert_eq!(f.hits("/stable/latest.json"), 1);
    assert_eq!(f.hits("/dev/latest.json"), 0);
}

#[test]
fn download_verifies_and_returns_the_file() {
    let sha = kiln_manage::hash::sha256_hex(PAYLOAD);
    let f = feed("0.2.0-dev.2", PAYLOAD, Some(&sha));
    let app = app("0.2.0-dev.1");
    let progress = Arc::new(Mutex::new(None));
    let (got, failures) = run(acquire(app.handle(), &[f.source("frostdev.io")], Channel::Dev, progress.clone()));
    let (source, acq) = got.unwrap();
    assert_eq!(source, "frostdev.io");
    assert_eq!(acq.bytes, PAYLOAD);
    assert!(failures.is_empty());
    let p = progress.lock().unwrap().clone().unwrap();
    assert_eq!((p.received, p.total, p.source.as_str()), (PAYLOAD.len() as u64, Some(PAYLOAD.len() as u64), "frostdev.io"));
}

#[test]
fn a_bad_signature_on_frostdev_falls_back_to_github() {
    let (f, g) = (feed("0.2.0-dev.2", &tampered(), None), feed("0.2.0-dev.2", PAYLOAD, None));
    let app = app("0.2.0-dev.1");
    let (got, failures) = run(acquire(app.handle(), &[f.source("frostdev.io"), g.source("GitHub")], Channel::Dev, Arc::new(Mutex::new(None))));
    let (source, acq) = got.unwrap();
    assert_eq!(source, "GitHub");
    assert_eq!(acq.bytes, PAYLOAD);
    assert_eq!(steps(&failures), [("frostdev.io", Step::Signature)]);
    assert_eq!(f.hits("/files/kiln.AppImage"), 1);
    assert_eq!(g.hits("/files/kiln.AppImage"), 1);
}

#[test]
fn a_bad_signature_is_never_accepted() {
    let (f, g) = (feed("0.2.0-dev.2", &tampered(), None), feed("0.2.0-dev.2", &tampered(), None));
    let app = app("0.2.0-dev.1");
    let (got, failures) = run(acquire(app.handle(), &[f.source("frostdev.io"), g.source("GitHub")], Channel::Dev, Arc::new(Mutex::new(None))));
    assert!(got.is_none());
    assert_eq!(steps(&failures), [("frostdev.io", Step::Signature), ("GitHub", Step::Signature)]);
    assert_eq!(failures[0].message, "the signature does not match the file");
}

#[test]
fn a_file_signed_for_another_version_is_refused() {
    // the manifest claims 0.2.0-dev.3; the file is signed as 0.2.0-dev.2 (an old release under a new number)
    let f = feed("0.2.0-dev.3", PAYLOAD, None);
    let app = app("0.2.0-dev.1");
    let (got, failures) = run(acquire(app.handle(), &[f.source("frostdev.io")], Channel::Dev, Arc::new(Mutex::new(None))));
    assert!(got.is_none());
    assert_eq!(steps(&failures), [("frostdev.io", Step::Signature)]);
    assert!(failures[0].message.contains("signed for 0.2.0-dev.2"), "{}", failures[0].message);
}

#[test]
fn a_failed_download_or_a_wrong_sha256_falls_back() {
    let g = feed("0.2.0-dev.2", PAYLOAD, None);
    let app = app("0.2.0-dev.1");
    // the manifest is there, the file is not
    let l = TcpListener::bind("127.0.0.1:0").unwrap();
    let port = l.local_addr().unwrap().port();
    let m = manifest("0.2.0-dev.2", &format!("http://127.0.0.1:{port}/files/missing.AppImage"), None);
    let f = Server::serve(l, vec![("/dev/latest.json".into(), 200, m)]);
    let (got, failures) = run(acquire(app.handle(), &[f.source("frostdev.io"), g.source("GitHub")], Channel::Dev, Arc::new(Mutex::new(None))));
    assert_eq!(got.unwrap().0, "GitHub");
    assert_eq!(steps(&failures), [("frostdev.io", Step::Download)]);
    assert!(failures[0].message.contains("404"), "{}", failures[0].message);

    let wrong = "0".repeat(64);
    let f = feed("0.2.0-dev.2", PAYLOAD, Some(&wrong));
    let (got, failures) = run(acquire(app.handle(), &[f.source("frostdev.io"), g.source("GitHub")], Channel::Dev, Arc::new(Mutex::new(None))));
    assert_eq!(got.unwrap().0, "GitHub");
    assert_eq!(steps(&failures), [("frostdev.io", Step::Hash)]);
}

#[test]
fn nothing_newer_is_no_download() {
    let f = feed("0.2.0-dev.2", PAYLOAD, None);
    let app = app("0.2.0-dev.2");
    let (got, failures) = run(acquire(app.handle(), &[f.source("frostdev.io")], Channel::Dev, Arc::new(Mutex::new(None))));
    assert!(got.is_none());
    assert_eq!(failures[0].message, "it offers no newer version");
    assert_eq!(f.hits("/files/kiln.AppImage"), 0);
}
