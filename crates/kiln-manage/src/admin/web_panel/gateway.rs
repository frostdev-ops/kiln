//! A server's web panel gateway: the browser's only way to that panel.
//!
//! - **Its own origin.** It listens on the server's loopback address ([`super::origins`]), bound and held by the
//!   launcher for the gateway's whole life (no port is looked up and released for someone else to take).
//! - **A pinned path.** Each browser connection is copied, bytes only, to the panel over the path the launcher
//!   checked the certificate through: a TCP connection to an address of this machine, or `ssh -W` to the remote
//!   server. The browser's TLS session ends at the panel, as before; nothing is decrypted here. A panel that listens on
//!   another machine's address (a network share's server folder) is refused: the browser's connection would cross
//!   the network without the launcher's pin.
//! - **This user only** (Linux): connections from another user's programs, or whose owner can't be found, are refused
//!   ([`super::peer`]).
//! - **The one-time login stays off command lines.** The browser is sent to a one-use bootstrap address on a second
//!   listener (`http://<origin ip>:<port>/k/<handle>`); only that page, fetched once by this user, holds the secret,
//!   and it moves the browser on with `location.replace`.
//! - **It closes by itself** when no browser connection is open for [`Limits::no_connection`] (the panel's tab was
//!   closed), when no byte moved for [`Limits::no_traffic`], when the launcher closes it, or when the launcher exits.

use super::peer;
use crate::admin::tunnel::ssh_stdio;
use crate::server::remote::RemoteSpec;
use crate::{Error, ErrorCode, Result};
use std::collections::HashMap;
use std::io::{Read, Write};
use std::net::{IpAddr, Ipv4Addr, Shutdown, SocketAddr, TcpListener, TcpStream};
use std::process::{Child, ChildStdin};
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

/// Where the panel is, seen from the launcher.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Upstream {
    /// An address of this machine (loopback, or one of its own interfaces).
    Direct(SocketAddr),
    /// The remote server's address as that machine sees it, through `ssh -W`.
    Ssh(RemoteSpec, SocketAddr),
}

#[derive(Debug, Clone, Copy)]
pub struct Limits {
    pub no_connection: Duration,
    pub no_traffic: Duration,
    pub handle_ttl: Duration,
    pub max_connections: usize,
}

impl Default for Limits {
    fn default() -> Self {
        Limits { no_connection: Duration::from_secs(120), no_traffic: Duration::from_secs(600), handle_ttl: Duration::from_secs(60), max_connections: 64 }
    }
}

/// Whether `ip` is this machine's: loopback, or a unicast address one of its interfaces holds (a socket can be bound
/// to it). Traffic to such an address never leaves the machine. An IPv4-mapped IPv6 address counts as its IPv4
/// address; the unspecified, broadcast and multicast addresses are nobody's (Linux binds a UDP socket to a broadcast
/// or multicast address too).
pub fn is_local_address(ip: IpAddr) -> bool {
    let ip = ip.to_canonical();
    if ip.is_unspecified() || ip.is_multicast() || matches!(ip, IpAddr::V4(v4) if v4.is_broadcast()) {
        return false;
    }
    ip.is_loopback() || std::net::UdpSocket::bind(SocketAddr::new(ip, 0)).is_ok()
}

struct Shared {
    stop: AtomicBool,
    active: AtomicUsize,
    /// Milliseconds since `start` of the last byte moved / the last time the connection count fell to 0.
    last_bytes: AtomicU64,
    idle_since: AtomicU64,
    start: Instant,
    handles: Mutex<HashMap<String, (String, Instant)>>,
    /// Per open browser connection (by number): its sockets and its ssh child, to end them when the gateway closes.
    conns: Mutex<HashMap<u64, (Vec<TcpStream>, Option<Arc<Mutex<Child>>>)>>,
    next: AtomicU64,
}

impl Shared {
    fn now_ms(&self) -> u64 {
        self.start.elapsed().as_millis() as u64
    }
    fn touch(&self) {
        self.last_bytes.store(self.now_ms(), Ordering::Relaxed);
    }
}

pub struct Gateway {
    pub upstream: Upstream,
    addr: SocketAddr,
    boot: SocketAddr,
    /// False when the per-server address could not be bound (macOS without loopback aliases) and 127.0.0.1 is used.
    pub isolated: bool,
    shared: Arc<Shared>,
    threads: Vec<std::thread::JoinHandle<()>>,
}

impl Gateway {
    /// Binds `ip:port` (port 0 or taken: any free port of `ip`) and the bootstrap listener on `ip`, and starts.
    pub fn start(ip: Ipv4Addr, port: u16, upstream: Upstream, limits: Limits) -> Result<Gateway> {
        let (listener, isolated) = match bind(ip, port) {
            Ok(l) => (l, true),
            Err(e) if e.kind() == std::io::ErrorKind::AddrNotAvailable => (bind(Ipv4Addr::LOCALHOST, 0)?, false),
            Err(e) => return Err(e.into()),
        };
        let addr = listener.local_addr()?;
        let boot_l = TcpListener::bind(SocketAddr::new(addr.ip(), 0))?;
        let boot = boot_l.local_addr()?;
        listener.set_nonblocking(true)?;
        boot_l.set_nonblocking(true)?;
        let shared = Arc::new(Shared {
            stop: AtomicBool::new(false),
            active: AtomicUsize::new(0),
            last_bytes: AtomicU64::new(0),
            idle_since: AtomicU64::new(0),
            start: Instant::now(),
            handles: Mutex::new(HashMap::new()),
            conns: Mutex::new(HashMap::new()),
            next: AtomicU64::new(1),
        });
        let mut threads = Vec::new();
        {
            let (sh, up) = (shared.clone(), upstream.clone());
            threads.push(std::thread::Builder::new().name(format!("web-panel-{addr}")).spawn(move || accept_loop(listener, sh, up, limits))?);
        }
        {
            let sh = shared.clone();
            threads.push(std::thread::Builder::new().name(format!("web-panel-boot-{boot}")).spawn(move || boot_loop(boot_l, sh))?);
        }
        {
            let sh = shared.clone();
            threads.push(std::thread::Builder::new().name(format!("web-panel-idle-{addr}")).spawn(move || watchdog(sh, limits))?);
        }
        Ok(Gateway { upstream, addr, boot, isolated, shared, threads })
    }

    /// The panel's origin address (the browser's https://<this>).
    pub fn addr(&self) -> SocketAddr {
        self.addr
    }

    pub fn alive(&self) -> bool {
        !self.shared.stop.load(Ordering::Relaxed)
    }

    pub fn connections(&self) -> usize {
        self.shared.active.load(Ordering::Relaxed)
    }

    /// A one-use address that sends the browser on to `target` (which holds the one-time secret). Fails when the
    /// system gives no random bytes (never a guessable handle).
    pub fn offer(&self, target: String, ttl: Duration) -> Result<String> {
        let handle = crate::hash::hex(&crate::util::random_bytes::<32>()?);
        let mut h = self.shared.handles.lock().unwrap_or_else(|p| p.into_inner());
        h.retain(|_, (_, until)| *until > Instant::now());
        h.insert(handle.clone(), (target, Instant::now() + ttl));
        // a new open counts as activity: the browser is about to connect
        self.shared.idle_since.store(self.shared.now_ms(), Ordering::Relaxed);
        self.shared.touch();
        Ok(format!("http://{}/k/{handle}", self.boot))
    }

    /// Stops accepting, ends every browser connection and ssh child, and waits for the gateway's threads.
    pub fn close(mut self) {
        self.shutdown();
    }

    fn shutdown(&mut self) {
        stop(&self.shared);
        for t in self.threads.drain(..) {
            let _ = t.join();
        }
    }
}

impl Drop for Gateway {
    fn drop(&mut self) {
        self.shutdown();
    }
}

fn bind(ip: Ipv4Addr, port: u16) -> std::io::Result<TcpListener> {
    match TcpListener::bind((ip, port)) {
        Err(e) if port != 0 && e.kind() == std::io::ErrorKind::AddrInUse => TcpListener::bind((ip, 0)),
        r => r,
    }
}

fn stop(sh: &Shared) {
    if sh.stop.swap(true, Ordering::Relaxed) {
        return;
    }
    for (_, (socks, child)) in sh.conns.lock().unwrap_or_else(|p| p.into_inner()).drain() {
        for s in socks {
            let _ = s.shutdown(Shutdown::Both);
        }
        if let Some(c) = child {
            let mut c = c.lock().unwrap_or_else(|p| p.into_inner());
            let _ = c.kill();
            let _ = c.wait();
        }
    }
}

fn watchdog(sh: Arc<Shared>, limits: Limits) {
    while !sh.stop.load(Ordering::Relaxed) {
        std::thread::sleep(Duration::from_millis(100));
        let now = sh.now_ms();
        let idle_conn = sh.active.load(Ordering::Relaxed) == 0 && now.saturating_sub(sh.idle_since.load(Ordering::Relaxed)) > limits.no_connection.as_millis() as u64;
        let idle_bytes = now.saturating_sub(sh.last_bytes.load(Ordering::Relaxed)) > limits.no_traffic.as_millis() as u64;
        if idle_conn || idle_bytes {
            stop(&sh);
        }
    }
}

fn accept_loop(listener: TcpListener, sh: Arc<Shared>, up: Upstream, limits: Limits) {
    let local = listener.local_addr().ok();
    while !sh.stop.load(Ordering::Relaxed) {
        let (tcp, peer_addr) = match listener.accept() {
            Ok(a) => a,
            Err(_) => {
                std::thread::sleep(Duration::from_millis(25));
                continue;
            }
        };
        let _ = tcp.set_nonblocking(false);
        let _ = tcp.set_nodelay(true);
        // Linux: only this user's programs (a connection whose owner can't be found is refused too: a remote panel
        // takes whatever comes through the gateway's ssh as a loopback client)
        if !local.is_some_and(|l| peer::admits(l, peer_addr)) || sh.active.load(Ordering::Relaxed) >= limits.max_connections {
            let _ = tcp.shutdown(Shutdown::Both);
            continue;
        }
        let (sh2, up2) = (sh.clone(), up.clone());
        sh.active.fetch_add(1, Ordering::Relaxed);
        sh.touch();
        std::thread::spawn(move || {
            serve_one(tcp, &sh2, &up2);
            if sh2.active.fetch_sub(1, Ordering::Relaxed) == 1 {
                sh2.idle_since.store(sh2.now_ms(), Ordering::Relaxed);
            }
        });
    }
}

enum Sink {
    Tcp(TcpStream),
    Child(ChildStdin),
}

impl Sink {
    fn finish(self) {
        match self {
            Sink::Tcp(t) => {
                let _ = t.shutdown(Shutdown::Write);
            }
            Sink::Child(c) => drop(c),
        }
    }
}

impl Write for Sink {
    fn write(&mut self, b: &[u8]) -> std::io::Result<usize> {
        match self {
            Sink::Tcp(t) => t.write(b),
            Sink::Child(c) => c.write(b),
        }
    }
    fn flush(&mut self) -> std::io::Result<()> {
        match self {
            Sink::Tcp(t) => t.flush(),
            Sink::Child(c) => c.flush(),
        }
    }
}

fn copy_counted(r: &mut dyn Read, w: &mut dyn Write, sh: &Shared) {
    let mut buf = vec![0u8; 16 * 1024];
    loop {
        let n = match r.read(&mut buf) {
            Ok(0) | Err(_) => return,
            Ok(n) => n,
        };
        if w.write_all(&buf[..n]).and_then(|_| w.flush()).is_err() {
            return;
        }
        sh.touch();
    }
}

/// One browser connection: the upstream connection, then bytes both ways until both directions end.
fn serve_one(browser: TcpStream, sh: &Arc<Shared>, up: &Upstream) {
    let id = sh.next.fetch_add(1, Ordering::Relaxed);
    let mut socks = Vec::new();
    let (mut up_r, up_w, child): (Box<dyn Read + Send>, Sink, Option<Arc<Mutex<Child>>>) = match up {
        Upstream::Direct(target) => {
            // the target must still be this machine's (an address can move between opens)
            if !is_local_address(target.ip()) {
                return;
            }
            let Ok(t) = TcpStream::connect_timeout(target, Duration::from_secs(5)) else { return };
            let _ = t.set_nodelay(true);
            let (Ok(r), Ok(c)) = (t.try_clone(), t.try_clone()) else { return };
            socks.push(c);
            (Box::new(r), Sink::Tcp(t), None)
        }
        Upstream::Ssh(remote, target) => {
            let Ok(mut c) = ssh_stdio(remote, *target) else { return };
            let (Some(stdin), Some(stdout)) = (c.stdin.take(), c.stdout.take()) else { return };
            if let Some(err) = c.stderr.take() {
                std::thread::spawn(move || {
                    let _ = std::io::copy(&mut { err }, &mut std::io::sink());
                });
            }
            (Box::new(stdout), Sink::Child(stdin), Some(Arc::new(Mutex::new(c))))
        }
    };
    let (Ok(mut browser_r), Ok(c)) = (browser.try_clone(), browser.try_clone()) else { return };
    socks.push(c);
    sh.conns.lock().unwrap_or_else(|p| p.into_inner()).insert(id, (socks, child.clone()));
    if sh.stop.load(Ordering::Relaxed) {
        // closed meanwhile (its sweep may have run before this connection was listed): end this one too
        if let Some((socks, child)) = sh.conns.lock().unwrap_or_else(|p| p.into_inner()).remove(&id) {
            for s in socks {
                let _ = s.shutdown(Shutdown::Both);
            }
            if let Some(c) = child {
                let _ = c.lock().unwrap_or_else(|p| p.into_inner()).kill();
            }
        }
    }
    let mut browser_w = browser;
    let sh_up = sh.clone();
    let mut up_w = up_w;
    let upward = std::thread::spawn(move || {
        copy_counted(&mut browser_r, &mut up_w, &sh_up);
        up_w.finish();
    });
    copy_counted(&mut up_r, &mut browser_w, sh);
    let _ = browser_w.shutdown(Shutdown::Both);
    let _ = upward.join();
    if let Some(c) = child {
        let mut c = c.lock().unwrap_or_else(|p| p.into_inner());
        let _ = c.kill();
        let _ = c.wait();
    }
    sh.conns.lock().unwrap_or_else(|p| p.into_inner()).remove(&id);
}

// ------------------------------------------------------------------------------------------------- bootstrap

/// The page that moves the browser on: `location.replace` (no history entry for the bootstrap address), a meta
/// refresh without JavaScript. Its CSP allows exactly its own script.
pub fn bootstrap_page(target: &str) -> (String, String) {
    let script = format!("location.replace({});", serde_json::to_string(target).unwrap_or_default());
    use base64::Engine;
    use sha2::Digest;
    let hash = base64::engine::general_purpose::STANDARD.encode(sha2::Sha256::digest(script.as_bytes()));
    let attr: String = target.chars().map(|c| match c {
        '&' => "&amp;".to_string(),
        '"' => "&quot;".to_string(),
        '<' => "&lt;".to_string(),
        '>' => "&gt;".to_string(),
        c => c.to_string(),
    }).collect();
    let html = format!(
        "<!doctype html><html lang=en><meta charset=utf-8><meta name=referrer content=no-referrer><title>Web panel</title>\
         <script>{script}</script><noscript><meta http-equiv=refresh content=\"0;url={attr}\"></noscript>\
         <p style=\"font:16px system-ui\">Opening the web panel…</p></html>"
    );
    (html, format!("default-src 'none'; script-src 'sha256-{hash}'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'"))
}

fn respond(mut s: &TcpStream, status: &str, ctype: &str, csp: Option<&str>, body: &str) {
    let mut head = format!(
        "HTTP/1.1 {status}\r\nContent-Type: {ctype}\r\nContent-Length: {}\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\n\
         X-Content-Type-Options: nosniff\r\nConnection: close\r\n",
        body.len()
    );
    if let Some(c) = csp {
        head.push_str(&format!("Content-Security-Policy: {c}\r\n"));
    }
    head.push_str("\r\n");
    let _ = s.write_all(head.as_bytes());
    let _ = s.write_all(body.as_bytes());
    let _ = s.flush();
}

fn boot_loop(listener: TcpListener, sh: Arc<Shared>) {
    let local = listener.local_addr().ok();
    while !sh.stop.load(Ordering::Relaxed) {
        let Ok((s, peer_addr)) = listener.accept() else {
            std::thread::sleep(Duration::from_millis(25));
            continue;
        };
        let sh = sh.clone();
        std::thread::spawn(move || {
            let served = serve_boot(s, peer_addr, local, |h| {
                let mut hs = sh.handles.lock().unwrap_or_else(|p| p.into_inner());
                hs.remove(h).filter(|(_, until)| *until > Instant::now()).map(|(t, _)| t)
            });
            if served {
                sh.touch();
            }
        });
    }
}

/// One request to a bootstrap listener on `local`: the page for `GET /k/<handle>` when `take` gives the handle's target
/// up (it is then gone), else the 404. Whether the page was served.
fn serve_boot(s: TcpStream, peer_addr: SocketAddr, local: Option<SocketAddr>, take: impl FnOnce(&str) -> Option<String>) -> bool {
    let _ = s.set_nonblocking(false);
    let _ = s.set_read_timeout(Some(Duration::from_secs(5)));
    let _ = s.set_write_timeout(Some(Duration::from_secs(5)));
    let mut req = Vec::new();
    let mut b = [0u8; 2048];
    while !req.windows(4).any(|w| w == b"\r\n\r\n") && req.len() < 8192 {
        match (&s).read(&mut b) {
            Ok(0) | Err(_) => return false,
            Ok(n) => req.extend_from_slice(&b[..n]),
        }
    }
    let line = String::from_utf8_lossy(&req[..req.iter().position(|&c| c == b'\r').unwrap_or(0)]).to_string();
    let mut parts = line.split(' ');
    let (method, path) = (parts.next().unwrap_or(""), parts.next().unwrap_or(""));
    let handle = path.strip_prefix("/k/").filter(|h| h.len() == 64 && h.bytes().all(|c| c.is_ascii_hexdigit()));
    // Linux: only a program of this user may take a page (another user could read the address from a
    // command line); elsewhere the address is as private as the browser's own command line
    let mine = local.is_some_and(|l| peer::admits(l, peer_addr));
    let target = match (method, handle) {
        ("GET", Some(h)) if mine => take(h),
        _ => None,
    };
    let served = target.is_some();
    match target {
        Some(t) => {
            let (html, csp) = bootstrap_page(&t);
            respond(&s, "200 OK", "text/html; charset=utf-8", Some(&csp), &html);
        }
        None => respond(
            &s,
            "404 Not Found",
            "text/plain; charset=utf-8",
            None,
            "This link was used already or has expired. Use \"Open web panel\" in the Kiln Launcher again.\n",
        ),
    }
    let _ = s.shutdown(Shutdown::Both);
    served
}

/// (0.2.0-dev.31.19) A one-use bootstrap address of its own, for a panel the browser reaches without a gateway (its
/// address on the internet, [`super::public`]): the same page and rules as a gateway's (one `GET` of
/// `http://127.0.0.1:<port>/k/<handle>`, from this user's programs only on Linux, for `ttl`), on a listener that closes
/// once the page is taken or `ttl` has passed. The one-time login in `target` stays off every command line.
pub fn offer_once(target: String, ttl: Duration) -> Result<String> {
    let handle = crate::hash::hex(&crate::util::random_bytes::<32>()?);
    let listener = TcpListener::bind(SocketAddr::new(IpAddr::V4(Ipv4Addr::LOCALHOST), 0))?;
    let addr = listener.local_addr()?;
    listener.set_nonblocking(true)?;
    let until = Instant::now() + ttl;
    let slot = Arc::new(Mutex::new(Some((handle.clone(), target))));
    std::thread::Builder::new().name(format!("web-panel-boot-{addr}")).spawn(move || {
        let open = |slot: &Mutex<Option<(String, String)>>| slot.lock().unwrap_or_else(|p| p.into_inner()).is_some();
        while Instant::now() < until && open(&slot) {
            let Ok((s, peer_addr)) = listener.accept() else {
                std::thread::sleep(Duration::from_millis(25));
                continue;
            };
            let slot = slot.clone();
            std::thread::spawn(move || {
                serve_boot(s, peer_addr, Some(addr), |h| {
                    let mut g = slot.lock().unwrap_or_else(|p| p.into_inner());
                    if Instant::now() < until && g.as_ref().is_some_and(|(k, _)| k == h) { g.take().map(|(_, t)| t) } else { None }
                })
            });
        }
        // the listener closes here: the address answers nothing any more
    })?;
    Ok(format!("http://{addr}/k/{handle}"))
}

/// The error for a panel on another machine's address.
pub fn not_local(addr: SocketAddr) -> Error {
    Error::new(
        ErrorCode::Unsupported,
        format!(
            "The web panel listens on {addr}, which is another machine. The launcher opens a panel only on this machine or \
             through SSH, so the browser's connection is the one it checked: add the server as a remote (SSH) server, or open \
             https://{addr} yourself and sign in with a password."
        ),
    )
}
