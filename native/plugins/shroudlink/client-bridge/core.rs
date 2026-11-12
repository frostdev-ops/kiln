// SPDX-License-Identifier: MIT
// Cooperative client ABI v1. No game addresses, sockets, file IO or fabricated UI.
// Copied by prepare.py into the operator's private MIT addon source; tested here
// against the same real shroudlink UI/frame codec used by the hosted engine.
use serde::Deserialize;
use serde_json::{json, Value};
use shroudlink::ui::{Event, MenuEntry, Node, ToClient, UiEvent, Window};
use std::collections::{BTreeMap, HashSet, VecDeque};

pub const MAX_REPLY: usize = 256 * 1024;
pub const MAX_REQUEST: usize = 8192;
const TTL: u64 = 2000;
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u32)]
pub enum Error {
    Small = 1,
    Invalid = 2,
    Inactive = 3,
    Stale = 4,
    Busy = 5,
    Unavailable = 6,
}
#[derive(Deserialize)]
#[serde(tag = "op", rename_all = "snake_case", deny_unknown_fields)]
enum Request {
    Read {},
    Acquire {
        module: String,
        session: String,
        client: String,
    },
    Release {
        module: String,
        session: String,
        lease: String,
    },
    Event {
        module: String,
        session: String,
        lease: String,
        revision: u64,
        owner: String,
        event: UiEvent,
    },
}
#[derive(Clone)]
struct Lease {
    client: String,
    token: String,
    until: u64,
}
#[derive(Clone)]
pub struct Ticket {
    module: String,
    session: String,
    lease: String,
    revision: u64,
    owner: String,
    event: UiEvent,
}
#[derive(Clone)]
pub struct Bridge {
    module: String,
    generation: u64,
    revision: u64,
    // Lease tokens are fresh random values that no reply but the acquiring one contains:
    // another in-process reader cannot derive one from module, session or a counter.
    secret: fn() -> Option<u128>,
    active: bool,
    welcomed: bool,
    fault: bool,
    world: bool,
    // This session has drawn a frame in a world, and the client update came back after more
    // than TTL since then: until the next frame, it is not known whether that world goes on.
    entered: bool,
    away: bool,
    client: usize,
    tick: Option<u64>,
    lease: Option<Lease>,
    entries: BTreeMap<String, Vec<MenuEntry>>,
    windows: BTreeMap<(String, String), Window>,
    queue: VecDeque<Ticket>,
}
fn short(s: &str) -> bool {
    !s.is_empty() && s.len() <= 128 && !s.contains('\0')
}
fn text(s: &str) -> bool {
    s.len() <= 4096 && !s.contains('\0')
}
impl Bridge {
    pub fn new(module: String, secret: fn() -> Option<u128>) -> Self {
        Self {
            module,
            generation: 0,
            revision: 0,
            secret,
            active: true,
            welcomed: false,
            fault: false,
            world: false,
            entered: false,
            away: false,
            client: 0,
            tick: None,
            lease: None,
            entries: BTreeMap::new(),
            windows: BTreeMap::new(),
            queue: VecDeque::new(),
        }
    }
    fn session(&self) -> String {
        self.generation.to_string()
    }
    fn bump(&mut self) {
        if let Some(n) = self.revision.checked_add(1) {
            self.revision = n;
        } else {
            self.fault = true;
        }
    }
    pub fn begin_session(&mut self) {
        if let Some(n) = self.generation.checked_add(1) {
            self.generation = n;
        } else {
            self.active = false;
        }
        self.welcomed = false;
        self.fault = false;
        self.world = false;
        self.entered = false;
        self.away = false;
        self.client = 0;
        self.tick = None;
        self.lease = None;
        self.entries.clear();
        self.windows.clear();
        self.queue.clear();
        self.bump();
    }
    pub fn welcome(&mut self, protocol: u64) {
        self.welcomed = self.generation != 0 && protocol == 2 && !self.fault;
    }
    pub fn stop(&mut self) {
        self.begin_session();
        self.active = false;
    }
    pub fn world(&mut self, present: bool, now: u64) {
        // Outside a world (screen 0): not ready and no ownership while it lasts. What this
        // session received stays, unless the session had been in a world and the client update
        // stopped for more than TTL since its last frame there: then the player left. A stall or
        // a title-bar drag draws no frame outside the world, a frame outside the world during
        // play has a fresh update, and a join's loading screen comes before `entered`. The
        // in-game UI draws no frames at the main menu, so a leave shows as the closing world's
        // last frames or as the next world's loading screen after its first update. A new
        // beacon, a client change or shutdown also start a new session.
        if present {
            self.entered = true;
            self.away = false;
        } else {
            self.end_lease();
            if self.entered && (self.away || self.stale(now)) {
                self.begin_session();
            }
        }
        self.world = present;
    }
    fn stale(&self, now: u64) -> bool {
        self.tick.is_some_and(|t| now < t || now - t > TTL)
    }
    fn end_lease(&mut self) {
        self.lease = None;
        self.queue.clear();
    }
    pub fn tick(&mut self, now: u64, client: usize) {
        if self.entered && self.stale(now) {
            self.away = true;
        }
        self.expire(now);
        if self.client != 0 && self.client != client {
            self.begin_session();
        }
        self.client = client;
        self.tick = Some(now);
        self.expire(now);
    }
    fn ready(&self, now: u64) -> bool {
        self.active
            && !self.fault
            && self.welcomed
            && self.world
            && !self.away
            && self.client != 0
            && self.tick.is_some_and(|t| now >= t && now - t <= TTL)
    }
    fn expire(&mut self, now: u64) {
        // A stale tick (a loading stall, a title-bar drag) means not ready while it lasts
        // (ready() checks its age) and ends ownership: the lease and its queue never come
        // back. Received state stays for the same session and client pointer; upstream
        // treats the same threshold as a live value that recovers.
        if self.stale(now)
            || self.lease.as_ref().is_some_and(|l| now >= l.until)
        {
            self.end_lease();
        }
    }
    pub fn presenting(&mut self, now: u64) -> bool {
        self.expire(now);
        self.ready(now) && self.lease.is_some()
    }
    fn epochs(&self, module: &str, session: &str) -> Result<(), Error> {
        if module != self.module || session != self.session() {
            Err(Error::Stale)
        } else {
            Ok(())
        }
    }
    fn owns(&self, lease: &str, now: u64) -> bool {
        self.ready(now)
            && self
                .lease
                .as_ref()
                .is_some_and(|l| l.token == lease && now < l.until)
    }
    fn state(&self, now: u64, own_lease: Option<&str>) -> Value {
        let ready = self.ready(now);
        json!({"abi":1, "module":self.module, "session":self.session(), "revision":self.revision,
            "ready":ready, "leased":self.lease.is_some(), "lease":own_lease,
            "entries":if ready { self.entries.iter().flat_map(|(o, es)| es.iter().map(move |e| json!({"owner":o,"id":e.id,"label":e.label}))).collect::<Vec<_>>() } else { vec![] },
            "windows":if ready { self.windows.iter().map(|((o,_),w)| json!({"owner":o,"window":w})).collect::<Vec<_>>() } else { vec![] }})
    }
    pub fn observe(&mut self, message: &ToClient) {
        if !self.active || self.generation == 0 || self.fault {
            return;
        }
        let mut next = self.clone();
        let valid = match message {
            ToClient::Entries { owner, entries } => {
                let mut ids = HashSet::new();
                let ok = short(owner)
                    && entries.len() <= 64
                    && entries
                        .iter()
                        .all(|e| short(&e.id) && text(&e.label) && ids.insert(&e.id));
                if ok {
                    next.entries.insert(owner.clone(), entries.clone());
                }
                ok
            }
            ToClient::Open { owner, window } => {
                let ok = short(owner) && valid_window(window);
                if ok {
                    next.windows
                        .insert((owner.clone(), window.id.clone()), window.clone());
                }
                ok
            }
            ToClient::Close { owner, id } => {
                next.windows.remove(&(owner.clone(), id.clone()));
                true
            }
            _ => return,
        };
        next.bump();
        let total_entries: usize = next.entries.values().map(Vec::len).sum();
        let size = serde_json::to_vec(
            &json!({"entries":next.entries,"windows":next.windows.values().collect::<Vec<_>>()}),
        )
        .map_or(usize::MAX, |v| v.len());
        if !valid
            || next.entries.len() > 64
            || total_entries > 128
            || next.windows.len() > 32
            || size > MAX_REPLY / 2
        {
            // Never keep an old actionable view after an unrepresentable replacement.
            self.begin_session();
            self.fault = true;
        } else {
            *self = next;
        }
    }
    fn valid_event(&self, owner: &str, event: &UiEvent) -> bool {
        if !short(owner) || !text(&event.node) || !text(&event.window) {
            return false;
        }
        if matches!(event.event, Event::Menu) {
            return event.window.is_empty()
                && self
                    .entries
                    .get(owner)
                    .is_some_and(|es| es.iter().any(|e| e.id == event.node));
        }
        let Some(w) = self.windows.get(&(owner.to_owned(), event.window.clone())) else {
            return false;
        };
        if matches!(event.event, Event::Closed) {
            return event.node.is_empty();
        }
        find_node(&w.content, &event.node).is_some_and(|node| match (node, &event.event) {
            (Node::Button { .. }, Event::Click { index: None }) => true,
            (Node::Toggle { .. }, Event::Change { value }) => value.is_boolean(),
            (Node::TextInput { .. }, Event::Change { value }) => {
                value.as_str().is_some_and(|s| s.len() <= 256 && text(s))
            }
            (Node::TextInput { .. }, Event::Submit { value }) => value.len() <= 256 && text(value),
            (Node::ItemGrid { items, .. }, Event::Click { index: Some(i) }) => {
                (*i as usize) < items.len()
            }
            (Node::List { entries, .. }, Event::Click { index: Some(i) }) => {
                (*i as usize) < entries.len()
            }
            (Node::Tabs { tabs, .. }, Event::Click { index: Some(i) }) => {
                (*i as usize) < tabs.len()
            }
            _ => false,
        })
    }
    fn request(&mut self, request: Request, now: u64) -> Result<Value, Error> {
        if !self.active {
            return Err(Error::Inactive);
        }
        self.expire(now);
        match request {
            Request::Read {} => Ok(self.state(now, None)),
            Request::Acquire {
                module,
                session,
                client,
            } => {
                self.epochs(&module, &session)?;
                if !self.ready(now) {
                    return Err(Error::Unavailable);
                }
                if !short(&client) {
                    return Err(Error::Invalid);
                }
                if self.lease.as_ref().is_some_and(|l| l.client != client) {
                    return Err(Error::Busy);
                }
                if self.lease.is_none() {
                    let token = (self.secret)().ok_or(Error::Unavailable)?;
                    self.lease = Some(Lease {
                        client,
                        token: format!("{token:032x}"),
                        until: now.saturating_add(TTL),
                    });
                } else {
                    self.lease.as_mut().unwrap().until = now.saturating_add(TTL);
                }
                Ok(self.state(now, self.lease.as_ref().map(|l| l.token.as_str())))
            }
            Request::Release {
                module,
                session,
                lease,
            } => {
                self.epochs(&module, &session)?;
                if !self.owns(&lease, now) {
                    return Err(Error::Stale);
                }
                self.lease = None;
                self.queue.clear();
                Ok(self.state(now, None))
            }
            Request::Event {
                module,
                session,
                lease,
                revision,
                owner,
                event,
            } => {
                self.epochs(&module, &session)?;
                if !self.owns(&lease, now) || revision != self.revision {
                    return Err(Error::Stale);
                }
                if !self.valid_event(&owner, &event)
                    || serde_json::to_vec(&event.to_message(&owner))
                        .map_or(true, |b| b.len() > 2048)
                {
                    return Err(Error::Invalid);
                }
                if self.queue.len() >= 32 {
                    return Err(Error::Busy);
                }
                self.queue.push_back(Ticket {
                    module,
                    session,
                    lease,
                    revision,
                    owner,
                    event,
                });
                Ok(json!({"abi":1,"queued":true}))
            }
        }
    }
    // Transactional: buffer probes / short buffers cannot acquire, renew, release or enqueue.
    pub fn rpc(
        &mut self,
        request: &[u8],
        capacity: usize,
        now: u64,
    ) -> Result<Vec<u8>, (Error, usize)> {
        if request.len() > MAX_REQUEST {
            return Err((Error::Invalid, 0));
        }
        let req: Request = serde_json::from_slice(request).map_err(|_| (Error::Invalid, 0))?;
        let mut next = self.clone();
        let response = next.request(req, now).map_err(|e| (e, 0))?;
        let bytes = serde_json::to_vec(&response).map_err(|_| (Error::Invalid, 0))?;
        if bytes.len() > MAX_REPLY {
            return Err((Error::Unavailable, 0));
        }
        if bytes.len() > capacity {
            return Err((Error::Small, bytes.len()));
        }
        *self = next;
        Ok(bytes)
    }
    pub fn valid_ticket(&self, t: &Ticket, now: u64) -> bool {
        self.epochs(&t.module, &t.session).is_ok()
            && self.owns(&t.lease, now)
            && t.revision == self.revision
            && self.valid_event(&t.owner, &t.event)
    }
    pub fn committed(&mut self, t: &Ticket, now: u64) -> Option<(String, String)> {
        if self.valid_ticket(t, now) && matches!(t.event.event, Event::Closed) {
            self.windows
                .remove(&(t.owner.clone(), t.event.window.clone()));
            self.bump();
            Some((t.owner.clone(), t.event.window.clone()))
        } else {
            None
        }
    }
    // Only called on the existing game-thread tick. A whole message must fit the
    // remaining packet budget: never leave partial bridge packets in the old outbox.
    pub fn take(&mut self, now: u64, byte_budget: usize) -> Option<(Ticket, Value)> {
        self.expire(now);
        while let Some(t) = self.queue.front() {
            if !self.valid_ticket(t, now) {
                self.queue.pop_front();
                continue;
            }
            let message = t.event.to_message(&t.owner);
            if serde_json::to_vec(&message).ok()?.len() > byte_budget {
                return None;
            }
            return Some((self.queue.pop_front().unwrap(), message));
        }
        None
    }
}
fn node_id(n: &Node) -> Option<&str> {
    match n {
        Node::Button { id, .. }
        | Node::Toggle { id, .. }
        | Node::TextInput { id, .. }
        | Node::ItemGrid { id, .. }
        | Node::List { id, .. }
        | Node::Tabs { id, .. } => Some(id),
        _ => None,
    }
}
fn find_node<'a>(nodes: &'a [Node], id: &str) -> Option<&'a Node> {
    nodes.iter().find_map(|n| {
        if node_id(n) == Some(id) {
            Some(n)
        } else if let Node::Row { children } = n {
            find_node(children, id)
        } else {
            None
        }
    })
}
fn valid_window(w: &Window) -> bool {
    fn walk(ns: &[Node], depth: usize, count: &mut usize, ids: &mut HashSet<String>) -> bool {
        if depth > 12 || ns.len() > 128 {
            return false;
        }
        for n in ns {
            *count += 1;
            if *count > 1024 {
                return false;
            }
            if let Some(id) = node_id(n) {
                if !short(id) || !ids.insert(id.to_owned()) {
                    return false;
                }
            }
            let ok = match n {
                Node::Text { text: t } | Node::Heading { text: t } => text(t),
                Node::Separator => true,
                Node::Row { children } => walk(children, depth + 1, count, ids),
                Node::Button { label, .. } | Node::Toggle { label, .. } => text(label),
                Node::TextInput { value, hint, .. } => {
                    value.len() <= 256 && text(value) && text(hint)
                }
                Node::ItemGrid { columns, items, .. } => {
                    *columns > 0 && *columns <= 16 && items.len() <= 128
                }
                Node::List { entries, .. } => {
                    entries.len() <= 128 && entries.iter().all(|s| text(s))
                }
                Node::Tabs { tabs, selected, .. } => {
                    !tabs.is_empty()
                        && tabs.len() <= 32
                        && (*selected as usize) < tabs.len()
                        && tabs.iter().all(|s| text(s))
                }
            };
            if !ok {
                return false;
            }
        }
        true
    }
    short(&w.id) && text(&w.title) && walk(&w.content, 0, &mut 0, &mut HashSet::new())
}

#[cfg(test)]
#[path = "tests.rs"]
mod tests;
