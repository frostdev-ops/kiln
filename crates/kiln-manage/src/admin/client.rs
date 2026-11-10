//! AdminClient: one connection to one server over the best transport: Tcp (endpoint.json on the same machine), Ssh (a
//! `ssh -N -L` tunnel to a remote endpoint), FileBridge (fallback; no push: state.json is polled and turned into
//! `state` pushes). Methods take `&self`: one client can serve calls from several threads while another thread reads
//! its events ([`super::hub::AdminHub`] does that).

use super::bridge::FileBridge;
use super::protocol::{self, Frame, KapErrorCode, ServerIdent};
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeSet, HashMap};
use std::net::TcpStream;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError, Sender};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Transport {
    Auto,
    Tcp,
    File,
    Ssh,
}

impl Transport {
    pub fn as_str(self) -> &'static str {
        match self {
            Transport::Auto => "auto",
            Transport::Tcp => "tcp",
            Transport::File => "file",
            Transport::Ssh => "ssh",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AdminSession {
    pub session: String,
    pub server_id: String,
    /// The transport in use (never Auto).
    pub transport: Transport,
    pub ops: Vec<String>,
    pub topics: Vec<String>,
    pub kiln: Option<String>,
    pub server: super::protocol::ServerIdent,
}

/// What a client delivers besides op results.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "lowercase")]
pub enum AdminEvent {
    Push { topic: String, seq: u64, data: serde_json::Value },
    /// The connection is gone. `retry`: the hub reconnects (a `ready` follows when it is back); false: final.
    Closed {
        reason: String,
        #[serde(default)]
        retry: bool,
    },
    Error { message: String },
    /// (Re)connected: the server's op and topic lists (AdminHub emits it after every connect).
    Ready { transport: Transport, ops: Vec<String>, topics: Vec<String> },
}

pub struct AdminClient {
    session: AdminSession,
    inner: Inner,
    events: Mutex<Receiver<AdminEvent>>,
}

enum Inner {
    Tcp(TcpLink),
    File(FileLink),
}

// ------------------------------------------------------------------------------------------------- TCP

struct Shared {
    waiters: Mutex<HashMap<u64, Sender<Frame>>>,
    events: Mutex<Option<Sender<AdminEvent>>>,
    closed: AtomicBool,
}

impl Shared {
    fn event(&self, e: AdminEvent) {
        if let Some(tx) = self.events.lock().unwrap().as_ref() {
            let _ = tx.send(e);
        }
    }

    fn close(&self, reason: &str) {
        if !self.closed.swap(true, Ordering::SeqCst) {
            self.event(AdminEvent::Closed { reason: reason.to_string(), retry: true });
        }
        self.waiters.lock().unwrap().clear();  // waiting callers see a dropped sender
    }
}

struct TcpLink {
    /// Every frame goes out through this one lock (requests, the reader's pongs, the keepalive's pings): two
    /// writers on clones of the socket could interleave the bytes of two frames.
    writer: Arc<Mutex<TcpStream>>,
    shared: Arc<Shared>,
    next_id: Arc<AtomicU64>,
    clock: Arc<LinkClock>,
    reader: Mutex<Option<JoinHandle<()>>>,
    keepalive: Mutex<Option<JoinHandle<()>>>,
    _tunnel: Option<super::tunnel::SshTunnel>,
}

/// When the link last sent and last received a frame (ms since it connected).
struct LinkClock {
    start: Instant,
    sent: AtomicU64,
    received: AtomicU64,
}

impl LinkClock {
    fn now(&self) -> u64 {
        self.start.elapsed().as_millis() as u64
    }

    fn mark_sent(&self) {
        self.sent.store(self.now(), Ordering::SeqCst);
    }

    fn mark_received(&self) {
        self.received.store(self.now(), Ordering::SeqCst);
    }
}

/// Writes one frame under the shared writer lock and notes the time.
fn send_on(writer: &Mutex<TcpStream>, clock: &LinkClock, f: &Frame) -> Result<()> {
    let mut w = writer.lock().unwrap();
    protocol::write_frame(&mut *w, f)?;
    clock.mark_sent();
    Ok(())
}

/// Where to reach a KAP listener and how to authenticate.
pub struct TcpTarget {
    pub host: String,
    pub port: u16,
    pub token: [u8; 32],
    pub server_id: String,
    /// Keeps an SSH tunnel alive as long as the connection.
    pub tunnel: Option<super::tunnel::SshTunnel>,
    pub transport: Transport,
}

impl TcpLink {
    fn connect(t: TcpTarget, events: Sender<AdminEvent>) -> Result<(TcpLink, AdminSession)> {
        use std::net::ToSocketAddrs;
        let addr = (t.host.as_str(), t.port)
            .to_socket_addrs()
            .map_err(|e| Error::new(ErrorCode::Network, format!("{}:{}: {e}", t.host, t.port)))?
            .next()
            .ok_or_else(|| Error::new(ErrorCode::Network, format!("{}:{}: no address", t.host, t.port)))?;
        let mut s = TcpStream::connect_timeout(&addr, Duration::from_secs(5))
            .map_err(|e| Error::new(ErrorCode::AdminUnavailable, format!("KAP {}:{}: {e}", t.host, t.port)))?;
        let _ = s.set_nodelay(true);
        s.set_read_timeout(Some(Duration::from_secs(protocol::HANDSHAKE_TIMEOUT_S + 1)))?;
        s.set_write_timeout(Some(Duration::from_secs(30)))?;
        let hello = read_frame(&mut s, protocol::MAX_SERVER_FRAME)?;
        let (nonce, ident, kiln) = match hello {
            Frame::Hello { proto, nonce, server, kiln, auth, .. } => {
                if proto != protocol::PROTOCOL {
                    return Err(Error::new(ErrorCode::Unsupported, format!("the server speaks {proto}, this launcher {}", protocol::PROTOCOL)));
                }
                if !auth.iter().any(|a| a == "hmac-sha256") {
                    return Err(Error::new(ErrorCode::Unsupported, "the server offers no hmac-sha256 authentication"));
                }
                (nonce, server, kiln)
            }
            Frame::Error { code, message, .. } => return Err(Error::new(protocol::error_code(Some(code)), format!("KAP: {message}"))),
            other => return Err(Error::new(ErrorCode::Parse, format!("KAP: expected hello, got {other:?}"))),
        };
        let cnonce = crate::hash::hex(&crate::util::random_bytes::<32>()?);
        let mac = protocol::mac(&t.token, protocol::AUTH_LABEL, &nonce, &cnonce);
        protocol::write_frame(
            &mut s,
            &Frame::Auth { proto: protocol::PROTOCOL.into(), cnonce: cnonce.clone(), mac, client: Some(format!("kiln-launcher/{}", crate::VERSION)) },
        )?;
        let (session, ops, topics, limits) = match read_frame(&mut s, protocol::MAX_SERVER_FRAME)? {
            Frame::Ready { session, mac, ops, topics, limits } => {
                let want = protocol::mac(&t.token, protocol::READY_LABEL, &nonce, &cnonce);
                if !ct_eq(want.as_bytes(), mac.to_ascii_lowercase().as_bytes()) {
                    return Err(Error::new(ErrorCode::AuthFailed, "the server could not prove it knows the admin token (ready mac)"));
                }
                (session, ops, topics, limits)
            }
            Frame::Error { code, message, .. } => {
                let ec = protocol::error_code(Some(code));
                return Err(Error::new(ec, if code == KapErrorCode::AuthFailed { "the admin token was refused (rotated? read it again)".to_string() } else { format!("KAP: {message}") }));
            }
            other => return Err(Error::new(ErrorCode::Parse, format!("KAP: expected ready, got {other:?}"))),
        };
        s.set_read_timeout(None)?;
        let shared = Arc::new(Shared { waiters: Mutex::new(HashMap::new()), events: Mutex::new(Some(events)), closed: AtomicBool::new(false) });
        let clock = Arc::new(LinkClock { start: Instant::now(), sent: AtomicU64::new(0), received: AtomicU64::new(0) });
        let mut rs = s.try_clone()?;
        let writer = Arc::new(Mutex::new(s));
        let next_id = Arc::new(AtomicU64::new(1));
        let max = limits.max_frame.max(protocol::MAX_CLIENT_FRAME);
        let (sh, w, ck) = (shared.clone(), writer.clone(), clock.clone());
        let reader = std::thread::Builder::new()
            .name(format!("kap-read {}", t.server_id))
            .spawn(move || reader_loop(&mut rs, &w, &ck, &sh, max))
            .map_err(|e| Error::new(ErrorCode::Internal, format!("spawning the KAP reader: {e}")))?;
        let timing = KeepaliveTiming::from_limits(&limits);
        let (sh, w, ck, ids) = (shared.clone(), writer.clone(), clock.clone(), next_id.clone());
        let keepalive = std::thread::Builder::new()
            .name(format!("kap-ping {}", t.server_id))
            .spawn(move || keepalive_loop(&w, &ck, &sh, &ids, timing))
            .map_err(|e| Error::new(ErrorCode::Internal, format!("spawning the KAP keepalive: {e}")))?;
        let link = TcpLink {
            writer,
            shared,
            next_id,
            clock,
            reader: Mutex::new(Some(reader)),
            keepalive: Mutex::new(Some(keepalive)),
            _tunnel: t.tunnel,
        };
        let session = AdminSession { session, server_id: t.server_id, transport: t.transport, ops, topics, kiln, server: ident };
        Ok((link, session))
    }

    fn send(&self, f: &Frame) -> Result<()> {
        if self.shared.closed.load(Ordering::SeqCst) {
            return Err(Error::new(ErrorCode::AdminUnavailable, "the KAP connection is closed"));
        }
        send_on(&self.writer, &self.clock, f).inspect_err(|_| self.shared.close("send failed"))
    }

    /// Sends a request-like frame (req/sub/unsub) and waits for its `res` (or an `error` with its id).
    fn request(&self, make: impl FnOnce(u64) -> Frame, timeout: Duration) -> Result<Frame> {
        let id = self.next_id.fetch_add(1, Ordering::SeqCst);
        let (tx, rx) = mpsc::channel();
        self.shared.waiters.lock().unwrap().insert(id, tx);
        if let Err(e) = self.send(&make(id)) {
            self.shared.waiters.lock().unwrap().remove(&id);
            return Err(e);
        }
        match rx.recv_timeout(timeout) {
            Ok(f) => Ok(f),
            Err(RecvTimeoutError::Timeout) => {
                self.shared.waiters.lock().unwrap().remove(&id);
                Err(Error::new(ErrorCode::Timeout, format!("no answer within {} s", timeout.as_secs())))
            }
            Err(RecvTimeoutError::Disconnected) => Err(Error::new(ErrorCode::AdminUnavailable, "the KAP connection closed")),
        }
    }

    fn shutdown(&self) {
        if !self.shared.closed.load(Ordering::SeqCst) {
            let _ = protocol::write_frame(&mut *self.writer.lock().unwrap(), &Frame::Bye { reason: "client".into() });
        }
        self.shared.closed.store(true, Ordering::SeqCst);
        let _ = self.writer.lock().unwrap().shutdown(std::net::Shutdown::Both);
        if let Some(h) = self.reader.lock().unwrap().take() {
            let _ = h.join();
        }
        if let Some(h) = self.keepalive.lock().unwrap().take() {
            h.thread().unpark();
            let _ = h.join();
        }
        *self.shared.events.lock().unwrap() = None;
    }
}

/// How often the keepalive sends and how long a silent server may stay silent (`ready.limits`, admin-protocol.md
/// section 5).
#[derive(Debug, Clone, Copy)]
struct KeepaliveTiming {
    ping: Duration,
    idle: Duration,
    tick: Duration,
}

impl KeepaliveTiming {
    fn from_limits(l: &protocol::Limits) -> KeepaliveTiming {
        let ping_ms = l.ping_s.clamp(1, protocol::PING_INTERVAL_S) * 1000;
        let idle_ms = (l.idle_s * 1000).max(ping_ms * 2);
        KeepaliveTiming { ping: Duration::from_millis(ping_ms), idle: Duration::from_millis(idle_ms), tick: Duration::from_millis((ping_ms / 4).clamp(50, 1000)) }
    }
}

/// The link's own heartbeat, on a native thread so it never depends on the webview's (throttled) timers or on what
/// the open page polls. It sends a `ping` once the link has sent nothing for `t.ping`, so the server never sees a
/// silent peer. When the server has sent nothing for `t.idle` (pushes, pongs and its own pings all count), the link is
/// dead: it closes it, and the hub reconnects.
fn keepalive_loop(writer: &Mutex<TcpStream>, clock: &LinkClock, sh: &Shared, ids: &AtomicU64, t: KeepaliveTiming) {
    while !sh.closed.load(Ordering::SeqCst) {
        std::thread::park_timeout(t.tick);
        if sh.closed.load(Ordering::SeqCst) {
            break;
        }
        let now = clock.now();
        if now.saturating_sub(clock.received.load(Ordering::SeqCst)) > t.idle.as_millis() as u64 {
            sh.close(&format!("the server was silent for {} s", t.idle.as_secs()));
            let _ = writer.lock().unwrap().shutdown(std::net::Shutdown::Both);
            break;
        }
        if now.saturating_sub(clock.sent.load(Ordering::SeqCst)) >= t.ping.as_millis() as u64 {
            let ts = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_millis() as u64).ok();
            let ping = Frame::Ping { id: ids.fetch_add(1, Ordering::SeqCst), ts };
            if send_on(writer, clock, &ping).is_err() {
                sh.close("send failed");
                break;
            }
        }
    }
}

fn ct_eq(a: &[u8], b: &[u8]) -> bool {
    a.len() == b.len() && a.iter().zip(b).fold(0u8, |acc, (x, y)| acc | (x ^ y)) == 0
}

fn read_frame(s: &mut TcpStream, max: u32) -> Result<Frame> {
    loop {
        let payload = protocol::read_payload(s, max)?.ok_or_else(|| Error::new(ErrorCode::AdminUnavailable, "the server closed the connection"))?;
        if let Some(f) = protocol::decode_known(&payload)? {
            return Ok(f);
        }
    }
}

fn reader_loop(r: &mut TcpStream, w: &Mutex<TcpStream>, clock: &LinkClock, sh: &Shared, max: u32) {
    let reason = loop {
        let payload = match protocol::read_payload(r, max) {
            Ok(Some(p)) => p,
            Ok(None) => break "the server closed the connection".to_string(),
            Err(e) => break if sh.closed.load(Ordering::SeqCst) { "closed".to_string() } else { e.message },
        };
        clock.mark_received();
        let frame = match protocol::decode_known(&payload) {
            Ok(Some(f)) => f,
            Ok(None) => continue,  // a newer frame type: ignored (kap/1 rule)
            Err(e) => break format!("bad frame from the server: {}", e.message),
        };
        match frame {
            Frame::Res { id, .. } => {
                if let Some(tx) = sh.waiters.lock().unwrap().remove(&id) {
                    let _ = tx.send(frame);
                }
            }
            Frame::Error { id: Some(id), .. } if sh.waiters.lock().unwrap().contains_key(&id) => {
                if let Some(tx) = sh.waiters.lock().unwrap().remove(&id) {
                    let _ = tx.send(frame);
                }
            }
            Frame::Error { code, message, .. } => sh.event(AdminEvent::Error { message: format!("{}: {message}", serde_json::to_value(code).ok().and_then(|v| v.as_str().map(String::from)).unwrap_or_default()) }),
            Frame::Push { topic, seq, data } => sh.event(AdminEvent::Push { topic, seq, data }),
            Frame::Ping { id, ts } => {
                let _ = send_on(w, clock, &Frame::Pong { id, ts });
            }
            Frame::Bye { reason } => break format!("bye: {reason}"),
            _ => {}
        }
    };
    sh.close(&reason);
}

// ------------------------------------------------------------------------------------------------- file bridge

struct FileLink {
    bridge: Arc<FileBridge>,
    topics: Arc<Mutex<BTreeSet<String>>>,
    stop: Arc<AtomicBool>,
    poller: Mutex<Option<JoinHandle<()>>>,
}

/// An Auto session that had to take the file bridge keeps looking for Kiln.Admin's own listener: the server may have
/// been starting still, and the bridge folder may be answered by another mod as well (the Emberwatch Lua mod under
/// ShroudForge writes its own state.json there, before Kiln.Admin is up). Once a KAP handshake succeeds the session
/// closes with `retry`, and the reconnect takes TCP.
struct Upgrade {
    server_dir: PathBuf,
    server_id: String,
    /// endpoint.json's mtime before the TCP attempt that failed
    seen: Option<std::time::SystemTime>,
}

/// endpoint.json is looked at this often; a full handshake is tried when it changed, and every UPGRADE_RETRY anyway.
const UPGRADE_LOOK: Duration = Duration::from_secs(2);
const UPGRADE_RETRY: Duration = Duration::from_secs(30);

fn endpoint_mtime(server_dir: &Path) -> Option<std::time::SystemTime> {
    std::fs::metadata(super::discover::admin_dir(server_dir).join(protocol::ENDPOINT_FILE)).and_then(|m| m.modified()).ok()
}

impl Upgrade {
    /// True when Kiln.Admin answers a handshake now (the probe's own connection is closed again).
    fn ready(&mut self, last_try: &mut Instant) -> bool {
        let m = endpoint_mtime(&self.server_dir);
        if m.is_none() || (m == self.seen && last_try.elapsed() < UPGRADE_RETRY) {
            return false;
        }
        self.seen = m;
        *last_try = Instant::now();
        AdminClient::connect_local_tcp(&self.server_dir, &self.server_id).is_ok()
    }
}

impl FileLink {
    fn open(bridge: FileBridge, server_id: String, events: Sender<AdminEvent>, upgrade: Option<Upgrade>) -> Result<(FileLink, AdminSession)> {
        let status = bridge.call("status", serde_json::Map::new(), Duration::from_secs(10))?;
        let ops: Vec<String> = status
            .get("ops")
            .and_then(|o| o.as_array())
            .map(|a| a.iter().filter_map(|x| x.as_str().map(String::from)).collect())
            .unwrap_or_default();
        let kiln = status.get("kiln").and_then(|k| k.as_str()).map(String::from);
        let bridge = Arc::new(bridge);
        let topics = Arc::new(Mutex::new(BTreeSet::new()));
        let stop = Arc::new(AtomicBool::new(false));
        let (b, t, st) = (bridge.clone(), topics.clone(), stop.clone());
        let poller = std::thread::Builder::new()
            .name(format!("kap-file {server_id}"))
            .spawn(move || file_poller(&b, &t, &st, events, upgrade))
            .map_err(|e| Error::new(ErrorCode::Internal, format!("spawning the bridge poller: {e}")))?;
        let session = AdminSession {
            session: format!("file-{}", crate::hash::hex(&crate::util::random_bytes::<6>()?)),
            server_id,
            transport: Transport::File,
            ops,
            topics: vec!["state".into()],
            kiln,
            server: ServerIdent::default(),
        };
        Ok((FileLink { bridge, topics, stop, poller: Mutex::new(Some(poller)) }, session))
    }

    fn shutdown(&self) {
        self.stop.store(true, Ordering::SeqCst);
        if let Some(h) = self.poller.lock().unwrap().take() {
            let _ = h.join();
        }
    }
}

/// state.json -> `state` pushes while subscribed; Closed when the server stops writing it for 15 s, and (an Auto
/// session) when Kiln.Admin's own listener answers.
fn file_poller(b: &FileBridge, topics: &Mutex<BTreeSet<String>>, stop: &AtomicBool, events: Sender<AdminEvent>, mut upgrade: Option<Upgrade>) {
    let mut last: Option<std::time::SystemTime> = None;
    let mut seq = 0u64;
    let mut stale_since: Option<Instant> = None;
    let mut looked = Instant::now();
    let mut tried = Instant::now();
    while !stop.load(Ordering::SeqCst) {
        std::thread::sleep(Duration::from_millis(250));
        if let Some(u) = upgrade.as_mut()
            && looked.elapsed() >= UPGRADE_LOOK
        {
            looked = Instant::now();
            if u.ready(&mut tried) {
                let _ = events.send(AdminEvent::Closed { reason: "Kiln.Admin's own connection is up; switching to it".into(), retry: true });
                return;
            }
        }
        let m = std::fs::metadata(b.dir.join("state.json")).and_then(|m| m.modified()).ok();
        if m.is_some() && m != last {
            last = m;
            stale_since = None;
            if topics.lock().unwrap().contains("state") {
                if let Ok(Some(data)) = b.state() {
                    seq += 1;
                    if events.send(AdminEvent::Push { topic: "state".into(), seq, data }).is_err() {
                        return;
                    }
                }
            }
        } else if b.state_age().is_none_or(|a| a > Duration::from_secs(15)) {
            let since = *stale_since.get_or_insert_with(Instant::now);
            if since.elapsed() > Duration::from_secs(1) {
                let _ = events.send(AdminEvent::Closed { reason: "the server stopped updating state.json".into(), retry: true });
                return;
            }
        }
    }
}

// ------------------------------------------------------------------------------------------------- AdminClient

impl AdminClient {
    /// Connects to a launcher-known server: local ones by their folder (Auto: Tcp when endpoint.json answers, else
    /// the file bridge when it is alive), remote ones through an SSH tunnel.
    pub fn connect(instance: &crate::server::instance::ServerInstance, transport: Transport) -> Result<AdminClient> {
        if let Some(remote) = instance.remote.as_ref() {
            return match transport {
                Transport::Auto | Transport::Ssh => AdminClient::connect_ssh(remote, &instance.id),
                Transport::Tcp | Transport::File => Err(Error::new(ErrorCode::Unsupported, "a remote server is reached through ssh")),
            };
        }
        let dir = instance.dir.as_deref().ok_or_else(|| Error::invalid(format!("server {} has no folder", instance.id)))?;
        AdminClient::connect_dir(dir, &instance.id, transport)
    }

    /// Connects to the server in `server_dir` (on this machine).
    pub fn connect_dir(server_dir: &Path, server_id: &str, transport: Transport) -> Result<AdminClient> {
        match transport {
            Transport::Tcp => AdminClient::connect_local_tcp(server_dir, server_id),
            Transport::File => AdminClient::connect_file(FileBridge::locate(server_dir)?, server_id),
            Transport::Ssh => Err(Error::new(ErrorCode::Unsupported, "ssh is for remote servers")),
            Transport::Auto => {
                // read before the attempt: a listener that comes up during it still counts as a change
                let seen = endpoint_mtime(server_dir);
                match AdminClient::connect_local_tcp(server_dir, server_id) {
                    Ok(c) => Ok(c),
                    Err(e) if e.code == ErrorCode::AuthFailed => Err(e),
                    Err(tcp) => {
                        let bridge = FileBridge::locate(server_dir)?;
                        if bridge.alive(Duration::from_secs(10)) {
                            let upgrade = Upgrade { server_dir: server_dir.to_path_buf(), server_id: server_id.into(), seen };
                            AdminClient::open_file(bridge, server_id, Some(upgrade))
                        } else {
                            Err(Error::new(ErrorCode::AdminUnavailable, format!("no admin transport reaches {server_id}: {}; the file bridge is idle", tcp.message))
                                .with_detail(serde_json::json!({ "tcp": tcp.message, "bridge": bridge.dir })))
                        }
                    }
                }
            }
        }
    }

    fn connect_local_tcp(server_dir: &Path, server_id: &str) -> Result<AdminClient> {
        let ep = super::discover::endpoint(server_dir)?
            .ok_or_else(|| Error::new(ErrorCode::AdminUnavailable, "no kiln/data/kiln-admin/endpoint.json (server stopped, or KAP off)"))?;
        let token = super::discover::token(server_dir)?;
        AdminClient::connect_tcp(TcpTarget { host: ep.host, port: ep.port, token, server_id: server_id.into(), tunnel: None, transport: Transport::Tcp })
    }

    /// A remote server: endpoint and token over `ssh cat`, then a tunnel to its listener.
    pub fn connect_ssh(remote: &crate::server::remote::RemoteSpec, server_id: &str) -> Result<AdminClient> {
        let ep = super::discover::remote_endpoint(remote)?
            .ok_or_else(|| Error::new(ErrorCode::AdminUnavailable, format!("{}: no endpoint.json (server stopped, or KAP off)", remote.host)))?;
        let token = super::discover::remote_token(remote)?;
        let tunnel = super::tunnel::SshTunnel::open(remote, ep.port)?;
        let port = tunnel.local_port;
        AdminClient::connect_tcp(TcpTarget { host: "127.0.0.1".into(), port, token, server_id: server_id.into(), tunnel: Some(tunnel), transport: Transport::Ssh })
    }

    pub fn connect_tcp(target: TcpTarget) -> Result<AdminClient> {
        let (tx, rx) = mpsc::channel();
        let (link, session) = TcpLink::connect(target, tx)?;
        Ok(AdminClient { session, inner: Inner::Tcp(link), events: Mutex::new(rx) })
    }

    pub fn connect_file(bridge: FileBridge, server_id: &str) -> Result<AdminClient> {
        AdminClient::open_file(bridge, server_id, None)
    }

    fn open_file(bridge: FileBridge, server_id: &str, upgrade: Option<Upgrade>) -> Result<AdminClient> {
        let (tx, rx) = mpsc::channel();
        let (link, session) = FileLink::open(bridge, server_id.into(), tx, upgrade)?;
        Ok(AdminClient { session, inner: Inner::File(link), events: Mutex::new(rx) })
    }

    pub fn session(&self) -> &AdminSession {
        &self.session
    }

    /// Runs an op and returns its `data`. Err(OpFailed) with the op's error text when it answers ok=false (detail:
    /// {op, transport, kapCode}); Busy / Timeout for those KAP codes.
    pub fn call(&self, op: &str, args: serde_json::Map<String, serde_json::Value>) -> Result<serde_json::Value> {
        let budget = protocol::op_budget(op);
        match &self.inner {
            Inner::File(f) => f.bridge.call(op, args, budget + Duration::from_secs(5)),
            Inner::Tcp(t) => {
                let op_s = op.to_string();
                let f = t.request(move |id| Frame::Req { id, op: op_s, args }, budget + Duration::from_secs(10))?;
                match f {
                    Frame::Res { ok: true, data, .. } => Ok(data.unwrap_or(serde_json::Value::Null)),
                    Frame::Res { error, code, .. } => {
                        let kap = code.map(|c| serde_json::to_value(c).unwrap_or_default());
                        Err(Error::new(protocol::error_code(code), error.unwrap_or_else(|| "the op failed".into()))
                            .with_detail(serde_json::json!({ "op": op, "transport": self.session.transport, "kapCode": kap })))
                    }
                    Frame::Error { code, message, .. } => Err(Error::new(protocol::error_code(Some(code)), message)),
                    other => Err(Error::new(ErrorCode::Internal, format!("unexpected answer {other:?}"))),
                }
            }
        }
    }

    pub fn subscribe(&self, topic: &str) -> Result<()> {
        self.sub(topic, true)
    }

    pub fn unsubscribe(&self, topic: &str) -> Result<()> {
        self.sub(topic, false)
    }

    fn sub(&self, topic: &str, on: bool) -> Result<()> {
        match &self.inner {
            Inner::File(f) => {
                if topic != "state" {
                    return Err(Error::new(ErrorCode::Unsupported, format!("the file bridge has no '{topic}' topic")));
                }
                let mut t = f.topics.lock().unwrap();
                if on {
                    t.insert(topic.into());
                } else {
                    t.remove(topic);
                }
                Ok(())
            }
            Inner::Tcp(t) => {
                let topic_s = topic.to_string();
                let f = t.request(
                    move |id| if on { Frame::Sub { id, topic: topic_s, every_ms: None } } else { Frame::Unsub { id, topic: topic_s } },
                    Duration::from_secs(10),
                )?;
                match f {
                    Frame::Res { ok: true, .. } => Ok(()),
                    Frame::Res { error, code, .. } => Err(Error::new(protocol::error_code(code), error.unwrap_or_else(|| format!("no topic {topic}")))),
                    other => Err(Error::new(ErrorCode::Internal, format!("unexpected answer {other:?}"))),
                }
            }
        }
    }

    /// The next push / close / error, waiting up to `timeout` (Ok(None) when nothing came).
    pub fn next_event(&self, timeout: std::time::Duration) -> Result<Option<AdminEvent>> {
        match self.events.lock().unwrap().recv_timeout(timeout) {
            Ok(e) => Ok(Some(e)),
            Err(RecvTimeoutError::Timeout) => Ok(None),
            Err(RecvTimeoutError::Disconnected) => Ok(Some(AdminEvent::Closed { reason: "closed".into(), retry: false })),
        }
    }

    /// Whether the connection is still up (TCP: no close seen; file bridge: the poller runs).
    pub fn is_open(&self) -> bool {
        match &self.inner {
            Inner::Tcp(t) => !t.shared.closed.load(Ordering::SeqCst),
            Inner::File(f) => !f.stop.load(Ordering::SeqCst) && f.poller.lock().unwrap().as_ref().is_some_and(|h| !h.is_finished()),
        }
    }

    /// Sends bye (TCP), stops the threads; the client stays usable for nothing but drop.
    pub fn shutdown(&self) {
        match &self.inner {
            Inner::Tcp(t) => t.shutdown(),
            Inner::File(f) => f.shutdown(),
        }
    }

    pub fn close(self) -> Result<()> {
        self.shutdown();
        Ok(())
    }

    /// Low level: send one frame (for tests and the mock server). TCP only.
    pub fn send_frame(&self, frame: &Frame) -> Result<()> {
        match &self.inner {
            Inner::Tcp(t) => t.send(frame),
            Inner::File(_) => Err(Error::new(ErrorCode::Unsupported, "the file bridge has no frames")),
        }
    }

    /// The file bridge folder in use (file transport).
    pub fn bridge_dir(&self) -> Option<PathBuf> {
        match &self.inner {
            Inner::File(f) => Some(f.bridge.dir.clone()),
            Inner::Tcp(_) => None,
        }
    }
}

impl Drop for AdminClient {
    fn drop(&mut self) {
        self.shutdown();
    }
}
