//! (0.2.0-dev.31.19) The check before "Open web panel" sends a one-time login to the panel's address on the internet
//! (`launcher-endpoint.json` `public`, Kilnwarden 0.1.4: hosted access `https://<name>.kiln.nexus/` or an own address).
//! The mod names that address only while it works; this check covers the seconds in which the file lags, and a name
//! that answers for someone else. Within [`TIMEOUT`] in all:
//! - the name resolves and a TCP connection opens;
//! - the TLS handshake (the name as SNI) passes only with the certificate `publicSha256` names: the panel whose file the
//!   launcher read, not another server that has the name now, nor anything in between;
//! - `GET /api/auth/status` answers 200 with `ownerExists` and `launcherLogin` true: the login can sign the owner in.
//!
//! Anything else and the panel opens on this computer, as before; [`Fallback`] says why, for the window. The check sends
//! no secret and no cookie.

use super::endpoint::PublicAddress;
use serde::Serialize;
use std::io::{ErrorKind, Read, Write};
use std::net::{SocketAddr, TcpStream, ToSocketAddrs};
use std::time::{Duration, Instant};

/// The whole check's budget: a hosted name answers through the relay in well under a second; a slower one is not worth
/// keeping the owner waiting for when this computer's route works.
pub const TIMEOUT: Duration = Duration::from_secs(4);

/// The most of the answer read (an auth status is a few hundred bytes).
const MAX_ANSWER: usize = 64 * 1024;

/// Why the panel opened on this computer although its file named a public address.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Fallback {
    /// No answer in time: the name doesn't resolve, nothing listens there, or it stalls.
    Unreachable,
    /// It answered, but not as this panel ready for the launcher's login: another certificate, a TLS alert, an HTTP
    /// error, no owner account yet, or the launcher's login switched off (auth.json `launcherLogin`).
    Refused,
}

/// Checks `public` (see the module): Ok when the launcher's login can go there.
pub fn check(public: &PublicAddress, timeout: Duration) -> Result<(), Fallback> {
    let deadline = Instant::now() + timeout;
    let left = || deadline.saturating_duration_since(Instant::now());
    let addrs = resolve(&public.host, public.port, left()).ok_or(Fallback::Unreachable)?;
    let socket = connect(&addrs, deadline).ok_or(Fallback::Unreachable)?;
    // every read and write waits at most until the deadline (rustls's handshake reads many times in one call)
    let mut io = Deadlined { socket: &socket, deadline };
    let config = super::pin::pinned_config(&public.sha256).map_err(|_| Fallback::Refused)?;
    let name = rustls::pki_types::ServerName::try_from(public.host.clone()).map_err(|_| Fallback::Refused)?;
    let mut tls = rustls::ClientConnection::new(config, name).map_err(|_| Fallback::Refused)?;
    while tls.is_handshaking() {
        tls.complete_io(&mut io).map_err(|e| failure(&e))?;
    }
    let host = if public.port == 443 { public.host.clone() } else { format!("{}:{}", public.host, public.port) };
    let mut stream = rustls::Stream::new(&mut tls, &mut io);
    stream
        .write_all(format!("GET /api/auth/status HTTP/1.1\r\nHost: {host}\r\nUser-Agent: kiln-launcher\r\nAccept: application/json\r\nConnection: close\r\n\r\n").as_bytes())
        .map_err(|e| failure(&e))?;
    let mut answer = Vec::new();
    let mut chunk = [0u8; 4096];
    loop {
        if Instant::now() >= deadline {
            return Err(Fallback::Unreachable);
        }
        match stream.read(&mut chunk) {
            Ok(0) => break,
            Ok(n) => {
                answer.extend_from_slice(&chunk[..n]);
                if answer.len() > MAX_ANSWER {
                    return Err(Fallback::Refused);
                }
                if complete(&answer) {
                    break;
                }
            }
            // closed without TLS's close_notify: what arrived is the answer
            Err(e) if e.kind() == ErrorKind::UnexpectedEof => break,
            Err(e) => return Err(failure(&e)),
        }
    }
    let (status, body) = parse(&answer).ok_or(Fallback::Refused)?;
    if status != 200 {
        return Err(Fallback::Refused);
    }
    let v: serde_json::Value = serde_json::from_slice(&body).map_err(|_| Fallback::Refused)?;
    if v["ownerExists"] == true && v["launcherLogin"] == true { Ok(()) } else { Err(Fallback::Refused) }
}

/// The name's addresses, given up after `within` (the system's resolver has no timeout of its own; a resolver that
/// hangs only keeps its own thread).
fn resolve(host: &str, port: u16, within: Duration) -> Option<Vec<SocketAddr>> {
    let (tx, rx) = std::sync::mpsc::channel();
    let host = host.to_string();
    std::thread::Builder::new()
        .name("web-panel-dns".into())
        .spawn(move || {
            let _ = tx.send((host.as_str(), port).to_socket_addrs().map(|a| a.collect::<Vec<_>>()));
        })
        .ok()?;
    match rx.recv_timeout(within) {
        Ok(Ok(v)) if !v.is_empty() => Some(v),
        _ => None,
    }
}

/// The most one address of several may take to open a connection before the next one is tried: a name usually has an
/// IPv6 and an IPv4 address (the relay's has), and a route that swallows connections (broken IPv6) must not use up the
/// whole check, as browsers try the next address after a moment too. The last address gets whatever time is left.
const PER_ADDRESS: Duration = Duration::from_secs(1);

/// A TCP connection to the first of `addrs` that answers before `deadline`; each address but the last gets its share
/// of the time left, at most [`PER_ADDRESS`].
pub(super) fn connect(addrs: &[SocketAddr], deadline: Instant) -> Option<TcpStream> {
    for (i, a) in addrs.iter().enumerate() {
        let left = deadline.saturating_duration_since(Instant::now());
        if left.is_zero() {
            break;
        }
        let rest = (addrs.len() - i) as u32;
        let within = if rest == 1 { left } else { (left / rest).min(PER_ADDRESS) };
        if let Ok(s) = TcpStream::connect_timeout(a, within) {
            return Some(s);
        }
    }
    None
}

/// The check's socket: each read and write waits only until the deadline (and past it fails as timed out), however
/// often it is called. A socket timeout set once would start again with every call, so a peer that sends a byte at a
/// time could keep the check (and the web panels' lock it runs under) for as long as it likes.
struct Deadlined<'a> {
    socket: &'a TcpStream,
    deadline: Instant,
}

impl Deadlined<'_> {
    fn left(&self) -> std::io::Result<Duration> {
        let left = self.deadline.saturating_duration_since(Instant::now());
        if left.is_zero() { Err(ErrorKind::TimedOut.into()) } else { Ok(left) }
    }
}

impl Read for Deadlined<'_> {
    fn read(&mut self, buf: &mut [u8]) -> std::io::Result<usize> {
        self.socket.set_read_timeout(Some(self.left()?))?;
        (&mut &*self.socket).read(buf)
    }
}

impl Write for Deadlined<'_> {
    fn write(&mut self, buf: &[u8]) -> std::io::Result<usize> {
        self.socket.set_write_timeout(Some(self.left()?))?;
        (&mut &*self.socket).write(buf)
    }

    fn flush(&mut self) -> std::io::Result<()> {
        Ok(())
    }
}

/// A TLS refusal (another certificate, an alert) is a refusal; a timeout or a dropped connection is no answer.
fn failure(e: &std::io::Error) -> Fallback {
    if e.kind() == ErrorKind::InvalidData { Fallback::Refused } else { Fallback::Unreachable }
}

/// The head of an HTTP/1.1 answer and where its body starts.
fn head(answer: &[u8]) -> Option<(&[u8], usize)> {
    let end = answer.windows(4).position(|w| w == b"\r\n\r\n")?;
    Some((&answer[..end], end + 4))
}

fn header<'a>(head: &'a str, name: &str) -> Option<&'a str> {
    head.split("\r\n").skip(1).find_map(|l| l.split_once(':').filter(|(k, _)| k.trim().eq_ignore_ascii_case(name)).map(|(_, v)| v.trim()))
}

/// Whether the whole answer is in: its Content-Length, or the last chunk.
fn complete(answer: &[u8]) -> bool {
    let Some((h, start)) = head(answer) else { return false };
    let h = String::from_utf8_lossy(h);
    if let Some(n) = header(&h, "content-length").and_then(|v| v.parse::<usize>().ok()) {
        return answer.len() >= start + n;
    }
    header(&h, "transfer-encoding").is_some_and(|v| v.eq_ignore_ascii_case("chunked")) && answer.ends_with(b"0\r\n\r\n")
}

/// The status and the body (a chunked one joined).
fn parse(answer: &[u8]) -> Option<(u16, Vec<u8>)> {
    let (h, start) = head(answer)?;
    let h = String::from_utf8_lossy(h);
    let status = h.split_whitespace().nth(1)?.parse().ok()?;
    if !h.starts_with("HTTP/1.") {
        return None;
    }
    let mut body = &answer[start..];
    if let Some(n) = header(&h, "content-length").and_then(|v| v.parse::<usize>().ok()) {
        body = body.get(..n)?;
        return Some((status, body.to_vec()));
    }
    if !header(&h, "transfer-encoding").is_some_and(|v| v.eq_ignore_ascii_case("chunked")) {
        return Some((status, body.to_vec()));
    }
    let mut out = Vec::new();
    loop {
        let line = body.windows(2).position(|w| w == b"\r\n")?;
        let size = usize::from_str_radix(std::str::from_utf8(&body[..line]).ok()?.split(';').next()?.trim(), 16).ok()?;
        body = &body[line + 2..];
        if size == 0 {
            return Some((status, out));
        }
        out.extend_from_slice(body.get(..size)?);
        body = body.get(size + 2..)?;
    }
}
