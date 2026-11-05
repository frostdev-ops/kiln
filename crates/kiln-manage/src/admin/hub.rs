//! AdminHub: the launcher's admin sessions, one per server. A session keeps its id across reconnects; when the
//! connection drops the hub emits `closed{retry: true}`, reconnects with backoff (1, 2, 4 ... 30 s), sends `sub` again
//! for every topic the session had, then emits `ready`. `disconnect` ends a session (`closed{retry: false}`). Events go
//! to one sink (the Tauri layer turns them into `kiln://admin`). Tauri-free, so it is tested here.

use super::client::{AdminClient, AdminEvent, AdminSession};
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::time::Duration;

/// Opens a client for a session (called again on every reconnect).
pub type Connector = Box<dyn Fn() -> Result<AdminClient> + Send + Sync>;

/// One event for the sink: `kiln://admin` `{session, serverId, event}`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HubEvent {
    pub session: String,
    pub server_id: String,
    pub event: AdminEvent,
}

pub type Sink = Arc<dyn Fn(HubEvent) + Send + Sync>;

struct Session {
    id: String,
    server_id: String,
    connector: Connector,
    client: Mutex<Option<Arc<AdminClient>>>,
    info: Mutex<AdminSession>,
    topics: Mutex<BTreeSet<String>>,
    stop: AtomicBool,
    wake: (Mutex<bool>, Condvar),
    pump: Mutex<Option<std::thread::JoinHandle<()>>>,
}

impl Session {
    fn emit(&self, sink: &Sink, event: AdminEvent) {
        sink(HubEvent { session: self.id.clone(), server_id: self.server_id.clone(), event });
    }

    /// Sleeps up to `d`, returns early (true) when the session is stopped.
    fn nap(&self, d: Duration) -> bool {
        let (m, cv) = &self.wake;
        let g = m.lock().unwrap();
        let _ = cv.wait_timeout_while(g, d, |woken| !*woken && !self.stop.load(Ordering::SeqCst)).unwrap();
        self.stop.load(Ordering::SeqCst)
    }
}

pub struct AdminHub {
    sessions: Mutex<BTreeMap<String, Arc<Session>>>,
    sink: Sink,
    next: AtomicU64,
    /// Reconnect backoff: first and largest delay.
    pub backoff: (Duration, Duration),
}

impl AdminHub {
    pub fn new(sink: impl Fn(HubEvent) + Send + Sync + 'static) -> AdminHub {
        AdminHub { sessions: Mutex::new(BTreeMap::new()), sink: Arc::new(sink), next: AtomicU64::new(1), backoff: (Duration::from_secs(1), Duration::from_secs(30)) }
    }

    /// The server's session: the existing one, else a new one (connects now; an error when that fails).
    pub fn connect(&self, server_id: &str, connector: Connector) -> Result<AdminSession> {
        if let Some(s) = self.find(server_id) {
            if !s.stop.load(Ordering::SeqCst) {
                return Ok(s.info.lock().unwrap().clone());
            }
            let _ = self.disconnect(&s.id);  // ended by itself (auth failure): replace it
        }
        let client = Arc::new(connector()?);
        let id = format!("adm-{}-{}", sanitize(server_id), self.next.fetch_add(1, Ordering::SeqCst));
        let mut info = client.session().clone();
        info.session = id.clone();
        let s = Arc::new(Session {
            id: id.clone(),
            server_id: server_id.to_string(),
            connector,
            client: Mutex::new(Some(client.clone())),
            info: Mutex::new(info.clone()),
            topics: Mutex::new(BTreeSet::new()),
            stop: AtomicBool::new(false),
            wake: (Mutex::new(false), Condvar::new()),
            pump: Mutex::new(None),
        });
        {
            let mut all = self.sessions.lock().unwrap();
            if let Some(existing) = all.values().find(|x| x.server_id == server_id && !x.stop.load(Ordering::SeqCst)) {
                // another thread won the race: keep its session
                client.shutdown();
                return Ok(existing.info.lock().unwrap().clone());
            }
            all.insert(id.clone(), s.clone());
        }
        s.emit(&self.sink, AdminEvent::Ready { transport: info.transport, ops: info.ops.clone(), topics: info.topics.clone() });
        let (sess, sink, backoff) = (s.clone(), self.sink.clone(), self.backoff);
        let h = std::thread::Builder::new()
            .name(format!("admin-hub {server_id}"))
            .spawn(move || pump(&sess, &sink, backoff))
            .map_err(|e| Error::new(ErrorCode::Internal, format!("spawning the admin pump: {e}")))?;
        *s.pump.lock().unwrap() = Some(h);
        Ok(info)
    }

    fn find(&self, server_id: &str) -> Option<Arc<Session>> {
        self.sessions.lock().unwrap().values().find(|s| s.server_id == server_id).cloned()
    }

    fn get(&self, session: &str) -> Result<Arc<Session>> {
        self.sessions.lock().unwrap().get(session).cloned().ok_or_else(|| Error::not_found(format!("no admin session {session}")))
    }

    fn client(&self, session: &str) -> Result<Arc<AdminClient>> {
        let s = self.get(session)?;
        let c = s.client.lock().unwrap().clone();
        c.ok_or_else(|| Error::new(ErrorCode::AdminUnavailable, format!("{}: reconnecting to the server", s.server_id)))
    }

    pub fn session_of(&self, server_id: &str) -> Option<AdminSession> {
        self.find(server_id).map(|s| s.info.lock().unwrap().clone())
    }

    pub fn sessions(&self) -> Vec<AdminSession> {
        self.sessions.lock().unwrap().values().map(|s| s.info.lock().unwrap().clone()).collect()
    }

    pub fn call(&self, session: &str, op: &str, args: serde_json::Map<String, serde_json::Value>) -> Result<serde_json::Value> {
        self.client(session)?.call(op, args)
    }

    pub fn subscribe(&self, session: &str, topic: &str) -> Result<()> {
        let s = self.get(session)?;
        if !s.info.lock().unwrap().topics.iter().any(|t| t == topic) {
            return Err(Error::invalid(format!("the server offers no '{topic}' topic")));
        }
        s.topics.lock().unwrap().insert(topic.to_string());
        match s.client.lock().unwrap().clone() {
            Some(c) => c.subscribe(topic),
            None => Ok(()),  // sent at the reconnect
        }
    }

    pub fn unsubscribe(&self, session: &str, topic: &str) -> Result<()> {
        let s = self.get(session)?;
        s.topics.lock().unwrap().remove(topic);
        match s.client.lock().unwrap().clone() {
            Some(c) => c.unsubscribe(topic),
            None => Ok(()),
        }
    }

    /// Ends a session: closes its connection, stops reconnecting, emits `closed{retry: false}`.
    pub fn disconnect(&self, session: &str) -> Result<()> {
        let s = self.sessions.lock().unwrap().remove(session).ok_or_else(|| Error::not_found(format!("no admin session {session}")))?;
        s.stop.store(true, Ordering::SeqCst);
        {
            let (m, cv) = &s.wake;
            *m.lock().unwrap() = true;
            cv.notify_all();
        }
        if let Some(c) = s.client.lock().unwrap().take() {
            c.shutdown();
        }
        if let Some(h) = s.pump.lock().unwrap().take() {
            let _ = h.join();
        }
        s.emit(&self.sink, AdminEvent::Closed { reason: "disconnected".into(), retry: false });
        Ok(())
    }

    /// Ends every session (launcher exit).
    pub fn shutdown(&self) {
        let ids: Vec<String> = self.sessions.lock().unwrap().keys().cloned().collect();
        for id in ids {
            let _ = self.disconnect(&id);
        }
    }
}

fn sanitize(s: &str) -> String {
    s.chars().map(|c| if c.is_ascii_alphanumeric() || c == '-' || c == '_' { c } else { '_' }).take(48).collect()
}

/// Forwards a session's events; reconnects when its connection drops.
fn pump(s: &Session, sink: &Sink, backoff: (Duration, Duration)) {
    while !s.stop.load(Ordering::SeqCst) {
        let Some(client) = s.client.lock().unwrap().clone() else { break };
        match client.next_event(Duration::from_millis(500)) {
            Ok(None) => continue,
            Ok(Some(AdminEvent::Closed { reason, .. })) => {
                *s.client.lock().unwrap() = None;
                client.shutdown();
                if s.stop.load(Ordering::SeqCst) {
                    break;
                }
                s.emit(sink, AdminEvent::Closed { reason, retry: true });
                if !reconnect(s, sink, backoff) {
                    break;
                }
            }
            Ok(Some(e)) => s.emit(sink, e),
            Err(e) => s.emit(sink, AdminEvent::Error { message: e.message }),
        }
    }
}

/// Retries until connected (true) or the session stops (false). An auth failure ends the session: the token was
/// rotated or the server was replaced; the user reconnects on purpose.
fn reconnect(s: &Session, sink: &Sink, backoff: (Duration, Duration)) -> bool {
    let mut delay = backoff.0;
    loop {
        if s.nap(delay) {
            return false;
        }
        match (s.connector)() {
            Ok(c) => {
                let c = Arc::new(c);
                let topics: Vec<String> = s.topics.lock().unwrap().iter().cloned().collect();
                for t in &topics {
                    if let Err(e) = c.subscribe(t) {
                        s.emit(sink, AdminEvent::Error { message: format!("re-subscribing {t}: {}", e.message) });
                    }
                }
                let mut info = c.session().clone();
                info.session = s.id.clone();
                *s.info.lock().unwrap() = info.clone();
                *s.client.lock().unwrap() = Some(c);
                s.emit(sink, AdminEvent::Ready { transport: info.transport, ops: info.ops, topics: info.topics });
                return true;
            }
            Err(e) if e.code == ErrorCode::AuthFailed => {
                s.emit(sink, AdminEvent::Closed { reason: e.message, retry: false });
                s.stop.store(true, Ordering::SeqCst);
                return false;
            }
            Err(_) => delay = (delay * 2).min(backoff.1),
        }
    }
}
