//! The host tools' HTTP layer for talking to release providers (`kiln publish`, `kiln updates check`).
//!
//! It is deliberately narrow:
//! - A [`Client`] belongs to one provider and reaches only the hosts of its [`Policy`], over https on port 443.
//! - A credential header goes only to a host the policy marks for it, and never follows a redirect.
//! - Redirects are refused. A single call may allow one redirect to hosts it names ([`Request::allow_redirect_to`]);
//!   that second request carries no credential, no cookie and no referrer.
//! - A [`Transport`] connects only to public addresses ([`policy::vet_addresses`]), uses no proxy from the
//!   environment and keeps no cookies.
//! - Request bodies can be streamed from an open file handle; responses are bounded, and a response over the bound
//!   is an error, not a cut-off body.
//! - A download ([`Client::download`]) passes the body of a success answer to a sink as it arrives instead of
//!   holding it in memory, under the same bound and the same host, credential and redirect rules.
//! - No message, `Debug` output or error holds a URL's query, a header value or a body.
//! - There is no DELETE: nothing built on this layer can remove anything at a provider.
//!
//! Tests use [`fake::FakeTransport`], which records every request, plays scripted responses and can lose a response
//! after the request was sent. The real transport ([`UreqTransport`], feature `http`) is only ever made by a command
//! the user ran; no test creates one.
//!
//! The older `nexus::api::Transport` (catalog and downloads) is separate and unchanged.

pub mod fake;
pub mod policy;

pub use policy::{Policy, Url};

use crate::{Error, ErrorCode, Result};
use std::io::{Read, Write};

/// The default bound of a response body.
pub const DEFAULT_MAX_RESPONSE: u64 = 1024 * 1024;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Method {
    Get,
    Post,
    Patch,
    Put,
}

impl Method {
    pub fn as_str(self) -> &'static str {
        match self {
            Method::Get => "GET",
            Method::Post => "POST",
            Method::Patch => "PATCH",
            Method::Put => "PUT",
        }
    }
}

/// A request body.
pub enum Body {
    Empty,
    Bytes(Vec<u8>),
    /// Exactly `len` bytes read from the handle's current position. The caller opened the file once and keeps
    /// responsibility for what it is (a frozen, verified file); the transport never reopens it by path.
    File { file: std::fs::File, len: u64 },
    /// Exactly `len` bytes from a reader (a hashing reader around a file handle, for example).
    Reader { reader: Box<dyn Read + Send>, len: u64 },
}

impl Body {
    pub fn len(&self) -> u64 {
        match self {
            Body::Empty => 0,
            Body::Bytes(b) => b.len() as u64,
            Body::File { len, .. } | Body::Reader { len, .. } => *len,
        }
    }

    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }
}

/// A request as a caller builds it.
pub struct Request {
    pub method: Method,
    pub url: String,
    pub headers: Vec<(String, String)>,
    pub body: Body,
    /// The response body may be at most this long.
    pub max_response: u64,
    /// Hosts one redirect of this call may lead to (GET without a body only). Empty: a redirect is an error.
    pub redirect_hosts: Vec<String>,
}

impl Request {
    pub fn new(method: Method, url: impl Into<String>) -> Request {
        Request { method, url: url.into(), headers: Vec::new(), body: Body::Empty, max_response: DEFAULT_MAX_RESPONSE, redirect_hosts: Vec::new() }
    }

    pub fn get(url: impl Into<String>) -> Request {
        Request::new(Method::Get, url)
    }

    pub fn header(mut self, name: &str, value: &str) -> Request {
        self.headers.push((name.to_string(), value.to_string()));
        self
    }

    pub fn body(mut self, body: Body) -> Request {
        self.body = body;
        self
    }

    pub fn json(self, value: &serde_json::Value) -> Request {
        self.header("Content-Type", "application/json").body(Body::Bytes(value.to_string().into_bytes()))
    }

    pub fn max_response(mut self, bytes: u64) -> Request {
        self.max_response = bytes;
        self
    }

    /// Allows one redirect of this call to exactly these hosts.
    pub fn allow_redirect_to(mut self, hosts: &[&str]) -> Request {
        self.redirect_hosts = hosts.iter().map(|h| h.to_ascii_lowercase()).collect();
        self
    }
}

impl std::fmt::Debug for Request {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Request").field("method", &self.method)
            .field("url", &self.url.split(['?', '#']).next().unwrap_or("<hidden>"))
            .field("headers", &"<hidden>").field("body", &"<hidden>").finish()
    }
}

/// A response. Status codes are data: a 4xx or 5xx is not an `Err`.
#[derive(Clone, Default)]
pub struct Response {
    pub status: u16,
    pub headers: Vec<(String, String)>,
    pub body: Vec<u8>,
}

impl Response {
    pub fn header(&self, name: &str) -> Option<&str> {
        self.headers.iter().find(|(k, _)| k.eq_ignore_ascii_case(name)).map(|(_, v)| v.as_str())
    }

    pub fn is_redirect(&self) -> bool {
        matches!(self.status, 301 | 302 | 303 | 307 | 308)
    }
}

impl std::fmt::Debug for Response {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        // Response headers and bodies can echo a credential or carry a signed URL.
        f.debug_struct("Response").field("status", &self.status).field("headers", &"<hidden>").field("body", &"<hidden>").finish()
    }
}

/// One request as a transport sees it: checked, with a parsed URL. A transport sends it to `url.host` on port 443
/// with TLS for that name, follows no redirect, and returns whatever status comes back.
pub struct WireRequest {
    pub method: Method,
    pub url: Url,
    pub headers: Vec<(String, String)>,
    pub body: Body,
    pub max_response: u64,
}

/// The wire. A transport must:
/// - resolve `url.host`, pass every address through [`policy::vet_addresses`] and connect only to what it returns
///   (on every new connection);
/// - follow no redirect, use no proxy from the environment, send and keep no cookies, add no header of its own
///   beyond what HTTP needs (`Host`, `Content-Length`);
/// - send exactly `body.len()` bytes and fail if the source ends early;
/// - fail with [`too_large`] when the response body is longer than `max_response`;
/// - put no URL query, header value or body into an error.
pub trait Transport: Send + Sync {
    fn exchange(&self, req: WireRequest) -> Result<Response>;

    /// [`Transport::exchange`] for a download. The body of a 2xx answer goes to `sink` as it arrives and is never
    /// held whole in memory: at most `max_response` bytes reach `sink`, and a longer body fails with [`too_large`]
    /// (what was written before stays written: the caller treats any error as "no download"). A body that ends
    /// before its stated length is an error, not a short download. The returned [`Response`] has an empty body. The
    /// body of any other status (a refusal, a redirect) is not read and never reaches `sink`: its status and headers
    /// are what a caller needs. Every other rule of `exchange` holds.
    ///
    /// The default refuses before anything is sent: a transport that cannot stream does not download.
    fn download(&self, req: WireRequest, sink: &mut dyn Write) -> Result<Response> {
        let _ = sink;
        Err(Error::new(ErrorCode::Refused, format!("This connection cannot stream a download from {}. Nothing was sent.", req.url.redacted())))
    }
}

/// The error for a response body over its bound.
pub fn too_large(url: &Url) -> Error {
    Error::new(ErrorCode::Refused, format!("The answer from {} was larger than allowed.", url.redacted()))
}

/// The error for a failed exchange. Transport diagnostics are dropped on purpose: they can hold the full URL.
pub fn network_error(url: &Url) -> Error {
    Error::new(ErrorCode::Network, format!("The request to {} failed. Check your connection and try again.", url.redacted()))
}

/// A provider's client: the policy checks, then the transport.
pub struct Client<'a> {
    policy: Policy,
    transport: &'a dyn Transport,
    user_agent: String,
}

impl<'a> Client<'a> {
    /// `user_agent` names the tool and its version only (`kiln/0.2.0`): nothing about the user or the machine.
    pub fn new(policy: Policy, transport: &'a dyn Transport, user_agent: &str) -> Client<'a> {
        Client { policy, transport, user_agent: user_agent.to_string() }
    }

    pub fn policy(&self) -> &Policy {
        &self.policy
    }

    /// Sends one request. Refused before anything is sent: a URL that breaks the rules, a host outside the policy, a
    /// credential header for a host not marked for it, a header the client owns, a redirect allowance on anything
    /// but a body-less GET. A redirect answer is an error unless this call allowed its target host and that host is
    /// one of the policy's; then it is followed once, without credentials.
    pub fn send(&self, req: Request) -> Result<Response> {
        self.dispatch(req, None)
    }

    /// [`Client::send`] for a download, with the same checks and the same redirect rule: the body of the final 2xx
    /// answer is written to `sink` as it arrives instead of being held in memory, at most `req.max_response` bytes
    /// (a longer body is an error). The returned [`Response`] has an empty body; another status comes back with its
    /// status and headers and nothing written to `sink`. Any error means the download did not happen, whatever
    /// `sink` received before it.
    pub fn download(&self, req: Request, sink: &mut dyn Write) -> Result<Response> {
        self.dispatch(req, Some(sink))
    }

    fn wire<'s>(&self, req: WireRequest, sink: Option<&mut (dyn Write + 's)>) -> Result<Response> {
        match sink {
            Some(sink) => self.transport.download(req, sink),
            None => self.transport.exchange(req),
        }
    }

    fn dispatch<'s>(&self, req: Request, mut sink: Option<&mut (dyn Write + 's)>) -> Result<Response> {
        let url = policy::parse_url(&req.url)?;
        let provider = &self.policy.provider;
        let rule = self.policy.rule(&url.host).ok_or_else(|| {
            Error::new(ErrorCode::Refused, format!("{} is not a host of {provider}. Nothing was sent.", url.host))
        })?;
        let mut headers = Vec::with_capacity(req.headers.len() + 1);
        for (k, v) in &req.headers {
            check_header(k, v)?;
            if policy::is_credential_header(k) && !rule.credentials {
                return Err(Error::new(ErrorCode::Refused, format!("A credential may not be sent to {}. Nothing was sent.", url.host)));
            }
            headers.push((k.clone(), v.clone()));
        }
        headers.push(("User-Agent".to_string(), self.user_agent.clone()));
        if !req.redirect_hosts.is_empty() && (req.method != Method::Get || !req.body.is_empty()) {
            return Err(Error::invalid("only a GET without a body may allow a redirect"));
        }
        let first = self.wire(WireRequest { method: req.method, url: url.clone(), headers: headers.clone(), body: req.body, max_response: req.max_response }, sink.as_deref_mut())?;
        if !first.is_redirect() {
            return Ok(first);
        }
        if req.redirect_hosts.is_empty() {
            return Err(Error::new(ErrorCode::Refused, format!("{} answered with a redirect, which is not followed.", url.redacted())));
        }
        let location = first.header("location").ok_or_else(|| network_error(&url))?;
        // Absolute https only: a relative Location would stay on the credential host.
        let target = policy::parse_url(location).map_err(|_| {
            Error::new(ErrorCode::Refused, format!("{} redirected to an address that is not allowed.", url.redacted()))
        })?;
        // The call names where it may go, and the provider's policy has to know that host too.
        if !req.redirect_hosts.contains(&target.host) || self.policy.rule(&target.host).is_none() {
            return Err(Error::new(ErrorCode::Refused, format!("{} redirected to a host that is not allowed.", url.redacted())));
        }
        // The second request starts from nothing: no credential, no cookie, no referrer, only what a download needs.
        let mut clean: Vec<(String, String)> =
            headers.into_iter().filter(|(k, _)| k.eq_ignore_ascii_case("accept") || k.eq_ignore_ascii_case("user-agent")).collect();
        clean.retain(|(k, _)| !policy::is_credential_header(k));
        let second = self.wire(WireRequest { method: Method::Get, url: target.clone(), headers: clean, body: Body::Empty, max_response: req.max_response }, sink)?;
        if second.is_redirect() {
            return Err(Error::new(ErrorCode::Refused, format!("{} redirected a second time, which is not followed.", target.redacted())));
        }
        Ok(second)
    }
}

fn check_header(name: &str, value: &str) -> Result<()> {
    let name_ok = !name.is_empty() && name.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'_');
    // Visible ASCII, space and tab: no CR/LF (a second header), no NUL.
    let value_ok = value.bytes().all(|b| b == b'\t' || (0x20..=0x7e).contains(&b));
    if !name_ok || !value_ok {
        return Err(Error::invalid("a request header has characters that are not allowed"));
    }
    if policy::is_forbidden_header(name) || name.eq_ignore_ascii_case("user-agent") || name.eq_ignore_ascii_case("content-length") || name.eq_ignore_ascii_case("transfer-encoding") {
        return Err(Error::invalid(format!("the header {name} is set by the client, not by a caller")));
    }
    Ok(())
}

/// Reads exactly `len` bytes from a source: it stops there, and a source that ends before is an error (a truncated
/// upload must fail, not be sent short).
pub struct Exactly<R: Read> {
    inner: R,
    left: u64,
}

impl<R: Read> Exactly<R> {
    pub fn new(inner: R, len: u64) -> Exactly<R> {
        Exactly { inner, left: len }
    }
}

impl<R: Read> Read for Exactly<R> {
    fn read(&mut self, buf: &mut [u8]) -> std::io::Result<usize> {
        if self.left == 0 || buf.is_empty() {
            return Ok(0);
        }
        let want = buf.len().min(usize::try_from(self.left).unwrap_or(usize::MAX));
        let n = self.inner.read(&mut buf[..want])?;
        if n == 0 {
            return Err(std::io::Error::new(std::io::ErrorKind::UnexpectedEof, "the body ended before its stated length"));
        }
        self.left -= n as u64;
        Ok(n)
    }
}

// ---------------------------------------------------------------- the real transport

/// The real transport (feature `http`): ureq with rustls, https only, no proxy, no redirects, no cookies, and a
/// resolver that lets only public addresses through.
#[cfg(feature = "http")]
pub struct UreqTransport {
    agent: ureq::Agent,
}

#[cfg(feature = "http")]
#[derive(Debug)]
struct VettingResolver(ureq::unversioned::resolver::DefaultResolver);

#[cfg(feature = "http")]
impl ureq::unversioned::resolver::Resolver for VettingResolver {
    fn resolve(
        &self,
        uri: &ureq::http::Uri,
        config: &ureq::config::Config,
        timeout: ureq::unversioned::transport::NextTimeout,
    ) -> std::result::Result<ureq::unversioned::resolver::ResolvedSocketAddrs, ureq::Error> {
        let found = self.0.resolve(uri, config, timeout)?;
        let ips: Vec<std::net::IpAddr> = found.iter().map(|a| a.ip()).collect();
        // The connector connects only to what this returns, with TLS for the URI's host name.
        if policy::vet_addresses(uri.host().unwrap_or(""), &ips).is_err() {
            return Err(ureq::Error::HostNotFound);
        }
        Ok(found)
    }
}

#[cfg(feature = "http")]
impl UreqTransport {
    pub fn new() -> UreqTransport {
        use std::time::Duration;
        let config = ureq::Agent::config_builder()
            .https_only(true)
            .http_status_as_error(false)
            .proxy(None)
            .max_redirects(0)
            .max_redirects_will_error(false)
            .redirect_auth_headers(ureq::config::RedirectAuthHeaders::Never)
            .timeout_connect(Some(Duration::from_secs(10)))
            .timeout_recv_response(Some(Duration::from_secs(60)))
            .timeout_per_read(Some(Duration::from_secs(60)))
            .build();
        let agent = ureq::Agent::with_parts(
            config,
            ureq::unversioned::transport::DefaultConnector::new(),
            VettingResolver(ureq::unversioned::resolver::DefaultResolver::default()),
        );
        UreqTransport { agent }
    }
}

/// A resolver for the timeout test: every name is this one address (a listener the test itself opened).
#[cfg(all(test, feature = "http"))]
#[derive(Debug)]
struct FixedResolver(std::net::SocketAddr);

#[cfg(all(test, feature = "http"))]
impl ureq::unversioned::resolver::Resolver for FixedResolver {
    fn resolve(&self, _: &ureq::http::Uri, _: &ureq::config::Config, _: ureq::unversioned::transport::NextTimeout)
        -> std::result::Result<ureq::unversioned::resolver::ResolvedSocketAddrs, ureq::Error> {
        let mut v = ureq::unversioned::resolver::ResolvedSocketAddrs::from_fn(|_| "0.0.0.0:1".parse().unwrap());
        v.push(self.0);
        Ok(v)
    }
}

#[cfg(feature = "http")]
impl UreqTransport {
    fn bounded_config(connect: std::time::Duration, response: std::time::Duration) -> ureq::config::Config {
        ureq::Agent::config_builder()
            .https_only(true)
            .http_status_as_error(false)
            .proxy(None)
            .max_redirects(0)
            .max_redirects_will_error(false)
            .redirect_auth_headers(ureq::config::RedirectAuthHeaders::Never)
            .timeout_connect(Some(connect))
            .timeout_recv_response(Some(response))
            .timeout_per_read(Some(response))
            // the whole exchange, whatever phase stalls (a TLS handshake that never completes, a body that trickles)
            .timeout_global(Some(connect + response))
            .build()
    }

    /// The same transport as [`UreqTransport::new`] with its own time bounds: `connect` for reaching the host,
    /// `response` for the answer, and `connect + response` for the whole exchange. For small reads that must not
    /// hold a check up (the update check: 10 s); uploads keep [`UreqTransport::new`].
    pub fn with_timeouts(connect: std::time::Duration, response: std::time::Duration) -> UreqTransport {
        let agent = ureq::Agent::with_parts(
            Self::bounded_config(connect, response),
            ureq::unversioned::transport::DefaultConnector::new(),
            VettingResolver(ureq::unversioned::resolver::DefaultResolver::default()),
        );
        UreqTransport { agent }
    }

    /// [`UreqTransport::with_timeouts`] with every name resolved to one address of the test's own.
    #[cfg(test)]
    fn with_timeouts_to(connect: std::time::Duration, response: std::time::Duration, addr: std::net::SocketAddr) -> UreqTransport {
        let agent = ureq::Agent::with_parts(Self::bounded_config(connect, response), ureq::unversioned::transport::DefaultConnector::new(), FixedResolver(addr));
        UreqTransport { agent }
    }
}

#[cfg(all(test, feature = "http"))]
mod timeout_tests {
    use super::*;
    use std::time::{Duration, Instant};

    /// A server that accepts and never answers: the call fails within its bound instead of hanging. The listener is
    /// this test's own, on 127.0.0.1; the host name in the URL is never looked up.
    #[test]
    fn a_host_that_never_answers_fails_within_the_bound() {
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let addr = listener.local_addr().unwrap();
        let accepted = std::thread::spawn(move || {
            // keep the connection open and silent until the client gives up
            let conn = listener.accept().map(|(s, _)| s);
            std::thread::sleep(Duration::from_secs(4));
            drop(conn);
        });
        let t = UreqTransport::with_timeouts_to(Duration::from_millis(500), Duration::from_millis(700), addr);
        let url = policy::parse_url("https://never-answers.example/releases?per_page=100").unwrap();
        let started = Instant::now();
        let r = t.exchange(WireRequest { method: Method::Get, url, headers: vec![("User-Agent".into(), "kiln-test/0".into())], body: Body::Empty, max_response: 1024 });
        let took = started.elapsed();
        let e = r.expect_err("a silent host gave an answer");
        assert_eq!(e.code, ErrorCode::Network);
        assert!(!e.message.contains("per_page"), "the error holds the query: {}", e.message);
        assert!(took >= Duration::from_millis(400), "failed at once ({took:?}): the listener was not reached");
        assert!(took < Duration::from_millis(3000), "took {took:?}, the bound is 1.2 s");
        let _ = accepted.join();
    }
}

#[cfg(feature = "http")]
impl Default for UreqTransport {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(feature = "http")]
impl Transport for UreqTransport {
    fn exchange(&self, req: WireRequest) -> Result<Response> {
        let (url, max) = (req.url.clone(), req.max_response);
        let mut resp = self.start(req)?;
        let (status, headers) = Self::head(&resp);
        let mut body = Vec::new();
        // One byte over the bound is read so that "exactly at the bound" and "over it" differ.
        let read = resp.body_mut().with_config().limit(max.saturating_add(1)).reader().read_to_end(&mut body);
        if body.len() as u64 > max {
            return Err(too_large(&url));
        }
        if read.is_err() {
            return Err(network_error(&url));
        }
        Ok(Response { status, headers, body })
    }

    fn download(&self, req: WireRequest, sink: &mut dyn Write) -> Result<Response> {
        let (url, max) = (req.url.clone(), req.max_response);
        let mut resp = self.start(req)?;
        let (status, headers) = Self::head(&resp);
        if !(200..300).contains(&status) {
            return Ok(Response { status, headers, body: Vec::new() });
        }
        // As in exchange, one byte over the bound is allowed through so that "over it" is seen, but never passed on.
        let mut reader = resp.body_mut().with_config().limit(max.saturating_add(1)).reader();
        let mut buf = vec![0u8; 64 * 1024];
        let mut total = 0u64;
        loop {
            let n = match reader.read(&mut buf) {
                Ok(0) => break,
                Ok(n) => n,
                Err(e) if e.kind() == std::io::ErrorKind::Interrupted => continue,
                // ureq fails a length-delimited body that ends early: a short download is an error here.
                Err(_) => return Err(network_error(&url)),
            };
            total = total.saturating_add(n as u64);
            if total > max {
                return Err(too_large(&url));
            }
            sink.write_all(&buf[..n]).map_err(|_| Error::new(ErrorCode::Io, format!("The download from {} could not be kept.", url.redacted())))?;
        }
        Ok(Response { status, headers, body: Vec::new() })
    }
}

#[cfg(feature = "http")]
impl UreqTransport {
    /// Sends one request and returns the answer with its body not yet read.
    fn start(&self, req: WireRequest) -> Result<ureq::http::Response<ureq::Body>> {
        let fail = |_e: ureq::Error| network_error(&req.url);
        let mut b = ureq::http::Request::builder().method(req.method.as_str()).uri(req.url.wire());
        for (k, v) in &req.headers {
            b = b.header(k.as_str(), v.as_str());
        }
        let len = req.body.len();
        let resp = match req.body {
            Body::Empty => {
                let r = b.body(()).map_err(|_| network_error(&req.url))?;
                self.agent.run(r).map_err(fail)?
            }
            Body::Bytes(bytes) => {
                let r = b.body(bytes).map_err(|_| network_error(&req.url))?;
                self.agent.run(r).map_err(fail)?
            }
            Body::File { file, .. } => {
                // A set Content-Length makes the body length-delimited, and sending fewer bytes than that fails.
                let r = b.header("Content-Length", len.to_string())
                    .body(ureq::SendBody::from_owned_reader(Exactly::new(file, len)))
                    .map_err(|_| network_error(&req.url))?;
                self.agent.run(r).map_err(fail)?
            }
            Body::Reader { reader, .. } => {
                let r = b.header("Content-Length", len.to_string())
                    .body(ureq::SendBody::from_owned_reader(Exactly::new(reader, len)))
                    .map_err(|_| network_error(&req.url))?;
                self.agent.run(r).map_err(fail)?
            }
        };
        Ok(resp)
    }

    fn head(resp: &ureq::http::Response<ureq::Body>) -> (u16, Vec<(String, String)>) {
        let headers = resp.headers().iter().map(|(k, v)| (k.as_str().to_string(), v.to_str().unwrap_or("").to_string())).collect();
        (resp.status().as_u16(), headers)
    }
}
