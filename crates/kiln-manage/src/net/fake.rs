//! The transport tests use: it records every request, answers from a script and never touches a socket.
//!
//! - [`FakeTransport::reply`] queues an answer for a method and URL (query included); answers for the same pair play
//!   in order. A request with no answer left is recorded as unexpected and fails.
//! - [`Reply::Lost`] records the request as sent and then fails like a dropped connection: the caller cannot know
//!   whether the provider acted. [`Reply::NotSent`] fails before anything is recorded as sent.
//! - [`FakeTransport::resolve`] gives a host its addresses; they pass through the same
//!   [`super::policy::vet_addresses`] the real transport uses, so a private address refuses the request here too.
//!   A host without an entry resolves to a public test address.
//! - [`FakeTransport::offline`] makes a transport for code that must not use the network at all: any request is
//!   recorded and fails, and the test asserts [`FakeTransport::requests`] is empty.
//! - A download ([`Transport::download`]) plays the same script; the body of a 2xx answer reaches the sink in chunks
//!   of [`DOWNLOAD_CHUNK`] bytes, as from a socket, and stops with the bound's error once it would pass the bound.

use super::{policy, Body, Method, Response, Transport, WireRequest};
use crate::{Error, ErrorCode, Result};
use std::collections::{HashMap, VecDeque};
use std::io::{Read, Write};
use std::net::IpAddr;
use std::sync::Mutex;

/// What the fake does with one request.
#[derive(Clone)]
pub enum Reply {
    Response(Response),
    /// The request reaches the provider; the answer does not reach us.
    Lost,
    /// The connection fails before the request is sent.
    NotSent,
}

impl Reply {
    pub fn status(status: u16) -> Reply {
        Reply::Response(Response { status, headers: Vec::new(), body: Vec::new() })
    }

    pub fn json(status: u16, body: &str) -> Reply {
        Reply::Response(Response { status, headers: vec![("Content-Type".into(), "application/json".into())], body: body.as_bytes().to_vec() })
    }

    pub fn bytes(status: u16, body: &[u8]) -> Reply {
        Reply::Response(Response { status, headers: Vec::new(), body: body.to_vec() })
    }

    pub fn redirect(status: u16, location: &str) -> Reply {
        Reply::Response(Response { status, headers: vec![("Location".into(), location.into())], body: Vec::new() })
    }

    /// Adds a response header (no effect on `Lost` and `NotSent`).
    pub fn with_header(mut self, name: &str, value: &str) -> Reply {
        if let Reply::Response(r) = &mut self {
            r.headers.push((name.into(), value.into()));
        }
        self
    }
}

/// A request the fake saw. Unlike the real types this one shows everything: tests assert on it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Recorded {
    pub method: Method,
    /// The whole URL, query included.
    pub url: String,
    pub host: String,
    pub headers: Vec<(String, String)>,
    pub body_len: u64,
    pub body_sha256: String,
    /// The body when it is at most 64 KiB, else empty.
    pub body: Vec<u8>,
    /// False for `Reply::NotSent` and for a request refused by the address check.
    pub sent: bool,
}

impl Recorded {
    pub fn header(&self, name: &str) -> Option<&str> {
        self.headers.iter().find(|(k, _)| k.eq_ignore_ascii_case(name)).map(|(_, v)| v.as_str())
    }
}

#[derive(Default)]
struct State {
    replies: HashMap<(Method, String), VecDeque<Reply>>,
    dns: HashMap<String, Vec<IpAddr>>,
    log: Vec<Recorded>,
    unexpected: Vec<String>,
    offline: bool,
}

#[derive(Default)]
pub struct FakeTransport {
    state: Mutex<State>,
}

impl FakeTransport {
    pub fn new() -> FakeTransport {
        FakeTransport::default()
    }

    /// A transport that answers nothing: for code paths that must make no request.
    pub fn offline() -> FakeTransport {
        let t = FakeTransport::default();
        t.state.lock().unwrap().offline = true;
        t
    }

    /// Queues an answer for `method` + `url` (the whole URL as it goes on the wire).
    pub fn reply(&self, method: Method, url: &str, reply: Reply) -> &FakeTransport {
        self.state.lock().unwrap().replies.entry((method, url.to_string())).or_default().push_back(reply);
        self
    }

    /// Sets the addresses a host resolves to.
    pub fn resolve(&self, host: &str, addrs: &[&str]) -> &FakeTransport {
        let addrs = addrs.iter().map(|a| a.parse().expect("an IP address")).collect();
        self.state.lock().unwrap().dns.insert(host.to_string(), addrs);
        self
    }

    /// Every request seen, in order.
    pub fn requests(&self) -> Vec<Recorded> {
        self.state.lock().unwrap().log.clone()
    }

    /// Requests that reached the wire (`sent`).
    pub fn sent(&self) -> Vec<Recorded> {
        self.requests().into_iter().filter(|r| r.sent).collect()
    }

    /// `METHOD url` of every request that had no scripted answer.
    pub fn unexpected(&self) -> Vec<String> {
        self.state.lock().unwrap().unexpected.clone()
    }

    /// Scripted answers nobody asked for, as `METHOD url`: a test that ends with some left did less than it claims.
    pub fn unused(&self) -> Vec<String> {
        let st = self.state.lock().unwrap();
        let mut out: Vec<String> = st.replies.iter().filter(|(_, q)| !q.is_empty()).map(|((m, u), _)| format!("{} {u}", m.as_str())).collect();
        out.sort();
        out
    }
}

fn read_body(body: Body) -> std::io::Result<(u64, String, Vec<u8>)> {
    use sha2::Digest;
    let len = body.len();
    let mut reader: Box<dyn Read> = match body {
        Body::Empty => Box::new(std::io::empty()),
        Body::Bytes(b) => Box::new(std::io::Cursor::new(b)),
        Body::File { file, len } => Box::new(super::Exactly::new(file, len)),
        Body::Reader { reader, len } => Box::new(super::Exactly::new(reader, len)),
    };
    let mut hasher = sha2::Sha256::new();
    let mut keep = Vec::new();
    let mut total = 0u64;
    let mut buf = [0u8; 64 * 1024];
    loop {
        let n = reader.read(&mut buf)?;
        if n == 0 {
            break;
        }
        hasher.update(&buf[..n]);
        total += n as u64;
        if total <= 64 * 1024 {
            keep.extend_from_slice(&buf[..n]);
        } else {
            keep.clear();
        }
    }
    debug_assert_eq!(total, len);
    Ok((total, crate::hash::hex(&hasher.finalize()), keep))
}

/// The size of the pieces a download's body reaches its sink in.
pub const DOWNLOAD_CHUNK: usize = 16 * 1024;

impl FakeTransport {
    /// Plays one request against the script: resolve and vet, record, consume the body. Returns the scripted answer
    /// with its body not yet held against the bound, or the error the request ends in.
    fn play(&self, req: WireRequest) -> Result<Response> {
        let wire = req.url.wire();
        let key = (req.method, wire.clone());
        let mut rec = Recorded {
            method: req.method,
            url: wire.clone(),
            host: req.url.host.clone(),
            headers: req.headers.clone(),
            body_len: 0,
            body_sha256: String::new(),
            body: Vec::new(),
            sent: false,
        };
        // Resolve and vet first, as the real transport does: a refused address means nothing is sent.
        let addrs = {
            let st = self.state.lock().unwrap();
            st.dns.get(&req.url.host).cloned().unwrap_or_else(|| vec!["93.184.215.14".parse().unwrap()])
        };
        if let Err(e) = policy::vet_addresses(&req.url.host, &addrs) {
            self.state.lock().unwrap().log.push(rec);
            return Err(e);
        }
        let reply = {
            let mut st = self.state.lock().unwrap();
            if st.offline { None } else { st.replies.get_mut(&key).and_then(|q| q.pop_front()) }
        };
        let Some(reply) = reply else {
            let mut st = self.state.lock().unwrap();
            st.unexpected.push(format!("{} {wire}", req.method.as_str()));
            st.log.push(rec);
            return Err(Error::new(ErrorCode::Network, format!("fake transport: no answer scripted for {} {}", req.method.as_str(), req.url.redacted())));
        };
        if matches!(reply, Reply::NotSent) {
            self.state.lock().unwrap().log.push(rec);
            return Err(super::network_error(&req.url));
        }
        // The body is consumed like a real send would: a source that ends early fails the request.
        let url = req.url.clone();
        match read_body(req.body) {
            Ok((len, sha, keep)) => {
                rec.body_len = len;
                rec.body_sha256 = sha;
                rec.body = keep;
            }
            Err(_) => {
                self.state.lock().unwrap().log.push(rec);
                return Err(super::network_error(&url));
            }
        }
        rec.sent = true;
        self.state.lock().unwrap().log.push(rec);
        match reply {
            Reply::Response(r) => Ok(r),
            Reply::Lost | Reply::NotSent => Err(super::network_error(&url)),
        }
    }
}

impl Transport for FakeTransport {
    fn exchange(&self, req: WireRequest) -> Result<Response> {
        let (url, max) = (req.url.clone(), req.max_response);
        let r = self.play(req)?;
        if r.body.len() as u64 > max {
            return Err(super::too_large(&url));
        }
        Ok(r)
    }

    fn download(&self, req: WireRequest, sink: &mut dyn Write) -> Result<Response> {
        let (url, max) = (req.url.clone(), req.max_response);
        let mut r = self.play(req)?;
        let body = std::mem::take(&mut r.body);
        if !(200..300).contains(&r.status) {
            return Ok(r);
        }
        let mut total = 0u64;
        for chunk in body.chunks(DOWNLOAD_CHUNK) {
            total += chunk.len() as u64;
            if total > max {
                return Err(super::too_large(&url));
            }
            sink.write_all(chunk).map_err(|_| Error::new(ErrorCode::Io, format!("The download from {} could not be kept.", url.redacted())))?;
        }
        Ok(r)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::net::{Client, Policy, Request};

    const API: &str = "api.provider.example";
    const UP: &str = "uploads.provider.example";
    const CDN: &str = "assets.provider-cdn.example";
    const TOKEN: &str = "ghp_FAKETOKEN0123456789";

    fn policy() -> Policy {
        Policy::new("Provider").credential_host(API).credential_host(UP).plain_host(CDN)
    }

    fn client(t: &FakeTransport) -> Client<'_> {
        Client::new(policy(), t, "kiln-test/0")
    }

    #[test]
    fn a_request_is_recorded_with_its_headers_and_body() {
        let t = FakeTransport::new();
        t.reply(Method::Post, "https://api.provider.example/releases", Reply::json(201, r#"{"id":7}"#));
        let r = client(&t)
            .send(Request::new(Method::Post, "https://api.provider.example/releases").header("Authorization", &format!("Bearer {TOKEN}")).json(&serde_json::json!({"tag": "v1"})))
            .unwrap();
        assert_eq!(r.status, 201);
        let log = t.requests();
        assert_eq!(log.len(), 1);
        assert!(log[0].sent);
        assert_eq!(log[0].host, API);
        assert_eq!(log[0].header("authorization"), Some(format!("Bearer {TOKEN}").as_str()));
        assert_eq!(log[0].header("user-agent"), Some("kiln-test/0"));
        assert_eq!(log[0].body, br#"{"tag":"v1"}"#);
        assert_eq!(log[0].body_sha256, crate::hash::sha256_hex(br#"{"tag":"v1"}"#));
        assert!(t.unexpected().is_empty() && t.unused().is_empty());
    }

    #[test]
    fn a_host_outside_the_policy_gets_nothing() {
        let t = FakeTransport::new();
        t.reply(Method::Get, "https://evil.example.com/x", Reply::status(200));
        let e = client(&t).send(Request::get("https://evil.example.com/x")).unwrap_err();
        assert_eq!(e.code, ErrorCode::Refused);
        assert!(t.requests().is_empty());
        // a look-alike host is another host
        for url in ["https://api.provider.example.evil.example.com/x", "https://xapi.provider.example/x", "https://provider.example/x"] {
            assert_eq!(client(&t).send(Request::get(url)).unwrap_err().code, ErrorCode::Refused, "{url}");
        }
        assert!(t.requests().is_empty());
    }

    #[test]
    fn a_credential_never_leaves_its_hosts() {
        let t = FakeTransport::new();
        t.reply(Method::Get, "https://assets.provider-cdn.example/f", Reply::status(200));
        for h in ["Authorization", "authorization", "Cookie", "X-Api-Key", "apikey"] {
            let e = client(&t).send(Request::get("https://assets.provider-cdn.example/f").header(h, TOKEN)).unwrap_err();
            assert_eq!(e.code, ErrorCode::Refused, "{h}");
            assert!(!format!("{e:?}").contains(TOKEN));
        }
        assert!(t.requests().is_empty());
        // without one the same request goes out
        assert_eq!(client(&t).send(Request::get("https://assets.provider-cdn.example/f")).unwrap().status, 200);
    }

    #[test]
    fn a_redirect_is_an_error_unless_the_call_allowed_its_host() {
        let t = FakeTransport::new();
        let url = "https://api.provider.example/assets/9";
        t.reply(Method::Get, url, Reply::redirect(302, "https://assets.provider-cdn.example/blob/9?sig=SIGNEDSECRET"));
        let e = client(&t).send(Request::get(url).header("Authorization", TOKEN)).unwrap_err();
        assert_eq!(e.code, ErrorCode::Refused);
        assert_eq!(t.requests().len(), 1, "the redirect target must not be asked");

        // allowed, but to another host than the answer names
        t.reply(Method::Get, url, Reply::redirect(302, "https://evil.example.com/blob"));
        let e = client(&t).send(Request::get(url).header("Authorization", TOKEN).allow_redirect_to(&[CDN])).unwrap_err();
        assert_eq!(e.code, ErrorCode::Refused);
        assert_eq!(t.requests().len(), 2);

        // not https, an IP literal, a relative path: all refused
        for loc in ["http://assets.provider-cdn.example/blob", "https://10.0.0.1/blob", "/blob/9", "https://assets.provider-cdn.example:8443/b"] {
            t.reply(Method::Get, url, Reply::redirect(307, loc));
            let e = client(&t).send(Request::get(url).allow_redirect_to(&[CDN])).unwrap_err();
            assert_eq!(e.code, ErrorCode::Refused, "{loc}");
        }
        assert!(t.requests().iter().all(|r| r.host == API));
    }

    #[test]
    fn an_allowed_redirect_is_followed_once_without_the_credential() {
        let t = FakeTransport::new();
        let url = "https://api.provider.example/assets/9";
        let target = "https://assets.provider-cdn.example/blob/9?sig=SIGNEDSECRET";
        t.reply(Method::Get, url, Reply::redirect(302, target));
        t.reply(Method::Get, target, Reply::bytes(200, b"payload"));
        let r = client(&t)
            .send(Request::get(url).header("Authorization", TOKEN).header("Accept", "application/octet-stream").header("X-Custom", "1").allow_redirect_to(&[CDN]))
            .unwrap();
        assert_eq!(r.body, b"payload");
        let log = t.requests();
        assert_eq!(log.len(), 2);
        assert_eq!(log[0].header("authorization"), Some(TOKEN));
        assert_eq!(log[1].url, target, "signed parameters are sent as given");
        assert_eq!(log[1].header("authorization"), None);
        assert_eq!(log[1].header("x-custom"), None);
        assert_eq!(log[1].header("accept"), Some("application/octet-stream"));
        assert_eq!(log[1].header("user-agent"), Some("kiln-test/0"));
        assert!(log[1].headers.iter().all(|(_, v)| !v.contains(TOKEN)));

        // a second redirect is not followed
        t.reply(Method::Get, url, Reply::redirect(302, target));
        t.reply(Method::Get, target, Reply::redirect(302, "https://assets.provider-cdn.example/again"));
        let e = client(&t).send(Request::get(url).allow_redirect_to(&[CDN])).unwrap_err();
        assert_eq!(e.code, ErrorCode::Refused);
        assert_eq!(t.requests().len(), 4);
        assert!(!format!("{e:?}").contains("SIGNEDSECRET"));
    }

    #[test]
    fn only_a_bodyless_get_may_allow_a_redirect() {
        let t = FakeTransport::new();
        let e = client(&t).send(Request::new(Method::Post, "https://api.provider.example/x").allow_redirect_to(&[CDN])).unwrap_err();
        assert_eq!(e.code, ErrorCode::InvalidArgument);
        let e = client(&t).send(Request::get("https://api.provider.example/x").body(Body::Bytes(vec![1])).allow_redirect_to(&[CDN])).unwrap_err();
        assert_eq!(e.code, ErrorCode::InvalidArgument);
        assert!(t.requests().is_empty());
    }

    #[test]
    fn a_name_that_resolves_to_a_private_address_is_not_asked() {
        let t = FakeTransport::new();
        t.reply(Method::Get, "https://api.provider.example/x", Reply::status(200));
        for addrs in [&["10.0.0.7"][..], &["140.82.112.5", "192.168.1.1"], &["::ffff:127.0.0.1"], &["169.254.169.254"], &["100.64.0.1"], &["fe80::1"]] {
            t.resolve(API, addrs);
            let e = client(&t).send(Request::get("https://api.provider.example/x").header("Authorization", TOKEN)).unwrap_err();
            assert_eq!(e.code, ErrorCode::Refused, "{addrs:?}");
        }
        assert!(t.sent().is_empty(), "nothing may reach the wire");
        t.resolve(API, &["140.82.112.5", "2606:50c0:8000::154"]);
        assert_eq!(client(&t).send(Request::get("https://api.provider.example/x")).unwrap().status, 200);
        // the redirect target is vetted too
        let url = "https://api.provider.example/assets/9";
        t.reply(Method::Get, url, Reply::redirect(302, "https://assets.provider-cdn.example/blob"));
        t.reply(Method::Get, "https://assets.provider-cdn.example/blob", Reply::status(200));
        t.resolve(CDN, &["127.0.0.1"]);
        assert_eq!(client(&t).send(Request::get(url).allow_redirect_to(&[CDN])).unwrap_err().code, ErrorCode::Refused);
        assert!(t.sent().iter().all(|r| r.host == API));
    }

    #[test]
    fn errors_and_debug_output_hold_no_query_and_no_secret() {
        let t = FakeTransport::new();
        let url = "https://uploads.provider.example/put/1?X-Signature=SIGNEDSECRET&name=a.zip";
        t.reply(Method::Put, url, Reply::Lost);
        let req = Request::new(Method::Put, url).header("Authorization", TOKEN).body(Body::Bytes(b"BODYSECRET".to_vec()));
        let shown = format!("{req:?}");
        assert!(!shown.contains("SIGNEDSECRET") && !shown.contains(TOKEN) && !shown.contains("BODYSECRET") && !shown.contains('?'), "{shown}");
        let e = client(&t).send(req).unwrap_err();
        assert_eq!(e.code, ErrorCode::Network);
        let shown = format!("{e:?} {e} {}", serde_json::to_string(&e).unwrap());
        assert!(!shown.contains("SIGNEDSECRET") && !shown.contains(TOKEN) && !shown.contains("name=a.zip"), "{shown}");
        // unscripted, not sent, too large, refused host: the same holds
        let other = "https://uploads.provider.example/put/2?X-Signature=SIGNEDSECRET";
        let e1 = client(&t).send(Request::new(Method::Put, other).header("Authorization", TOKEN)).unwrap_err();
        t.reply(Method::Put, other, Reply::NotSent);
        let e2 = client(&t).send(Request::new(Method::Put, other).header("Authorization", TOKEN)).unwrap_err();
        t.reply(Method::Put, other, Reply::bytes(200, &[0u8; 100]));
        let e3 = client(&t).send(Request::new(Method::Put, other).max_response(99)).unwrap_err();
        let e4 = client(&t).send(Request::get("https://evil.example.com/?token=SIGNEDSECRET")).unwrap_err();
        for e in [e1, e2, e3, e4] {
            let shown = format!("{e:?} {e}");
            assert!(!shown.contains("SIGNEDSECRET") && !shown.contains(TOKEN), "{shown}");
        }
        let r = Response { status: 200, headers: vec![("Location".into(), other.into())], body: TOKEN.as_bytes().to_vec() };
        assert!(!format!("{r:?}").contains("SECRET") && !format!("{r:?}").contains(TOKEN));
    }

    #[test]
    fn a_lost_answer_was_sent_and_a_failed_connection_was_not() {
        let t = FakeTransport::new();
        let url = "https://api.provider.example/releases";
        t.reply(Method::Post, url, Reply::Lost).reply(Method::Post, url, Reply::NotSent).reply(Method::Post, url, Reply::status(201));
        let c = client(&t);
        assert_eq!(c.send(Request::new(Method::Post, url)).unwrap_err().code, ErrorCode::Network);
        assert_eq!(c.send(Request::new(Method::Post, url)).unwrap_err().code, ErrorCode::Network);
        assert_eq!(c.send(Request::new(Method::Post, url)).unwrap().status, 201);
        let sent: Vec<bool> = t.requests().iter().map(|r| r.sent).collect();
        assert_eq!(sent, [true, false, true]);
        // the script is used up: one more is unexpected
        assert!(c.send(Request::new(Method::Post, url)).is_err());
        assert_eq!(t.unexpected(), ["POST https://api.provider.example/releases"]);
    }

    #[test]
    fn the_offline_transport_answers_nothing() {
        let t = FakeTransport::offline();
        t.reply(Method::Get, "https://api.provider.example/x", Reply::status(200));
        assert!(client(&t).send(Request::get("https://api.provider.example/x")).is_err());
        assert_eq!(t.requests().len(), 1);
        assert!(t.sent().is_empty());
        assert_eq!(t.unused().len(), 1);
    }

    #[test]
    fn a_response_over_its_bound_is_an_error_not_a_short_body() {
        let t = FakeTransport::new();
        let url = "https://api.provider.example/big";
        t.reply(Method::Get, url, Reply::bytes(200, &[7u8; 4096])).reply(Method::Get, url, Reply::bytes(200, &[7u8; 4097]));
        assert_eq!(client(&t).send(Request::get(url).max_response(4096)).unwrap().body.len(), 4096);
        assert_eq!(client(&t).send(Request::get(url).max_response(4096)).unwrap_err().code, ErrorCode::Refused);
    }

    #[test]
    fn a_file_body_is_streamed_from_the_open_handle_and_must_be_whole() {
        use std::io::{Seek, Write};
        let dir = std::env::temp_dir().join(format!("kiln-net-fake-{}", crate::util::uuid_v4().unwrap()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("artifact.zip");
        let data: Vec<u8> = (0..200_000u32).map(|i| (i % 251) as u8).collect();
        std::fs::File::create(&path).unwrap().write_all(&data).unwrap();
        let t = FakeTransport::new();
        let url = "https://uploads.provider.example/assets?name=a.zip";
        t.reply(Method::Post, url, Reply::status(201)).reply(Method::Post, url, Reply::status(201));

        let mut file = std::fs::File::open(&path).unwrap();
        // the file is replaced on disk after it was opened: the upload still reads the opened one
        std::fs::remove_file(&path).unwrap();
        std::fs::write(&path, b"something else").unwrap();
        file.rewind().unwrap();
        client(&t).send(Request::new(Method::Post, url).body(Body::File { file, len: data.len() as u64 })).unwrap();
        let rec = &t.requests()[0];
        assert_eq!(rec.body_len, data.len() as u64);
        assert_eq!(rec.body_sha256, crate::hash::sha256_hex(&data));

        // a source shorter than its stated length fails and counts as not sent
        let file = std::fs::File::open(&path).unwrap();
        let e = client(&t).send(Request::new(Method::Post, url).body(Body::File { file, len: 1000 })).unwrap_err();
        assert_eq!(e.code, ErrorCode::Network);
        assert!(!t.requests()[1].sent);
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn callers_cannot_set_the_headers_the_client_owns_or_inject_one() {
        let t = FakeTransport::new();
        let c = client(&t);
        for (k, v) in [("Host", "evil.example.com"), ("Referer", "https://x.example.com/"), ("User-Agent", "me"), ("Content-Length", "5"), ("Accept", "a\r\nAuthorization: x"), ("Bad Name", "v"), ("", "v")] {
            let e = c.send(Request::get("https://api.provider.example/x").header(k, v)).unwrap_err();
            assert_eq!(e.code, ErrorCode::InvalidArgument, "{k}");
        }
        assert!(t.requests().is_empty());
    }

    /// A sink that keeps what it got and how it got it.
    #[derive(Default)]
    struct Pieces {
        bytes: Vec<u8>,
        writes: usize,
    }

    impl std::io::Write for Pieces {
        fn write(&mut self, buf: &[u8]) -> std::io::Result<usize> {
            self.bytes.extend_from_slice(buf);
            self.writes += 1;
            Ok(buf.len())
        }
        fn flush(&mut self) -> std::io::Result<()> {
            Ok(())
        }
    }

    #[test]
    fn a_download_streams_a_success_body_to_the_sink_and_returns_no_body() {
        let t = FakeTransport::new();
        let url = "https://api.provider.example/assets/9";
        let data: Vec<u8> = (0..200_000u32).map(|i| (i % 253) as u8).collect();
        t.reply(Method::Get, url, Reply::bytes(200, &data));
        let mut sink = Pieces::default();
        let r = client(&t).download(Request::get(url).header("Authorization", TOKEN).max_response(data.len() as u64), &mut sink).unwrap();
        assert_eq!(r.status, 200);
        assert!(r.body.is_empty(), "a download returned its body in memory");
        assert_eq!(sink.bytes, data);
        assert!(sink.writes >= data.len() / DOWNLOAD_CHUNK, "the body reached the sink in {} piece(s)", sink.writes);
        assert_eq!(t.requests()[0].header("authorization"), Some(TOKEN));
    }

    #[test]
    fn a_download_over_its_bound_fails_and_never_passes_the_bound_to_the_sink() {
        let t = FakeTransport::new();
        let url = "https://api.provider.example/assets/9";
        let data = vec![5u8; 3 * DOWNLOAD_CHUNK + 1];
        t.reply(Method::Get, url, Reply::bytes(200, &data)).reply(Method::Get, url, Reply::bytes(200, &data));
        let mut sink = Pieces::default();
        let e = client(&t).download(Request::get(url).max_response(data.len() as u64 - 1), &mut sink).unwrap_err();
        assert_eq!(e.code, ErrorCode::Refused);
        assert!(sink.bytes.len() < data.len(), "the sink got {} bytes, more than the bound", sink.bytes.len());
        // exactly at the bound is fine
        let mut sink = Pieces::default();
        client(&t).download(Request::get(url).max_response(data.len() as u64), &mut sink).unwrap();
        assert_eq!(sink.bytes.len(), data.len());
    }

    #[test]
    fn a_refusal_or_a_redirect_body_never_reaches_the_sink() {
        let t = FakeTransport::new();
        let url = "https://api.provider.example/assets/9";
        t.reply(Method::Get, url, Reply::json(404, r#"{"message": "Not Found"}"#));
        t.reply(Method::Get, url, Reply::redirect(302, "https://assets.provider-cdn.example/blob/9?sig=SIGNEDSECRET").with_header("Content-Type", "text/html"));
        let mut sink = Pieces::default();
        let r = client(&t).download(Request::get(url), &mut sink).unwrap();
        assert_eq!((r.status, r.body.len()), (404, 0));
        // a redirect the call did not allow is refused, as with send
        assert_eq!(client(&t).download(Request::get(url), &mut sink).unwrap_err().code, ErrorCode::Refused);
        assert!(sink.bytes.is_empty() && sink.writes == 0);
    }

    #[test]
    fn a_download_follows_one_allowed_redirect_without_the_credential() {
        let t = FakeTransport::new();
        let url = "https://api.provider.example/assets/9";
        let target = "https://assets.provider-cdn.example/blob/9?sig=SIGNEDSECRET";
        t.reply(Method::Get, url, Reply::redirect(302, target).with_header("Content-Length", "0"));
        t.reply(Method::Get, target, Reply::bytes(200, b"payload"));
        let mut sink = Pieces::default();
        let req = Request::get(url).header("Authorization", &format!("Bearer {TOKEN}")).header("Accept", "application/octet-stream").allow_redirect_to(&[CDN]);
        let r = client(&t).download(req, &mut sink).unwrap();
        assert_eq!((r.status, sink.bytes.as_slice()), (200, &b"payload"[..]));
        let log = t.requests();
        assert_eq!(log.len(), 2);
        assert_eq!(log[0].header("authorization"), Some(format!("Bearer {TOKEN}").as_str()));
        assert_eq!((log[1].host.as_str(), log[1].url.as_str()), (CDN, target));
        assert_eq!(log[1].header("authorization"), None, "the redirect hop carried the credential");
        assert!(log[1].headers.iter().all(|(k, v)| !v.contains(TOKEN) && !policy::is_credential_header(k)));
        assert_eq!(log[1].header("accept"), Some("application/octet-stream"));
    }

    #[test]
    fn a_redirect_needs_the_call_and_the_policy_to_allow_its_host() {
        let t = FakeTransport::new();
        let url = "https://api.provider.example/assets/9";
        // the call allows a host the provider's policy does not have
        let elsewhere = "https://storage.elsewhere.example/blob/9";
        t.reply(Method::Get, url, Reply::redirect(302, elsewhere)).reply(Method::Get, url, Reply::redirect(302, elsewhere));
        t.reply(Method::Get, elsewhere, Reply::bytes(200, b"payload"));
        let mut sink = Pieces::default();
        let e = client(&t).download(Request::get(url).allow_redirect_to(&["storage.elsewhere.example"]), &mut sink).unwrap_err();
        assert_eq!(e.code, ErrorCode::Refused);
        let e = client(&t).send(Request::get(url).allow_redirect_to(&["storage.elsewhere.example"])).unwrap_err();
        assert_eq!(e.code, ErrorCode::Refused);
        assert!(t.requests().iter().all(|r| r.host == API), "a host outside the policy was asked");
        assert!(sink.bytes.is_empty());
        assert_eq!(t.unused(), [format!("GET {elsewhere}")]);
    }

    #[test]
    fn a_transport_that_cannot_stream_refuses_a_download_before_sending() {
        struct Buffered<'a>(&'a FakeTransport);
        impl Transport for Buffered<'_> {
            fn exchange(&self, req: WireRequest) -> Result<Response> {
                self.0.exchange(req)
            }
        }
        let t = FakeTransport::new();
        let url = "https://api.provider.example/assets/9";
        t.reply(Method::Get, url, Reply::bytes(200, b"payload"));
        let b = Buffered(&t);
        let mut sink = Pieces::default();
        let e = Client::new(policy(), &b, "kiln-test/0").download(Request::get(url), &mut sink).unwrap_err();
        assert_eq!(e.code, ErrorCode::Refused);
        assert!(t.requests().is_empty() && sink.bytes.is_empty());
    }
}
