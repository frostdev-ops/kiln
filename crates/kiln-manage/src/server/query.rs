//! Steam A2S_INFO on the query port (with the challenge round trip newer servers ask for),.

use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::net::{ToSocketAddrs, UdpSocket};
use std::time::{Duration, Instant};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ServerQuery {
    pub name: String,
    pub players: u32,
    pub max_players: u32,
    pub version: Option<String>,
    pub ping_ms: Option<u32>,
}

const REQUEST: &[u8] = b"\xff\xff\xff\xffTSource Engine Query\x00";

/// Parses an A2S_INFO answer (`\xff\xff\xff\xffI` ...).
pub fn parse_info(data: &[u8]) -> Option<ServerQuery> {
    if data.len() < 6 || &data[..5] != b"\xff\xff\xff\xffI" {
        return None;
    }
    let mut off = 6;
    let mut strings = Vec::new();
    for _ in 0..4 {
        let end = off + data.get(off..)?.iter().position(|&b| b == 0)?;
        strings.push(String::from_utf8_lossy(&data[off..end]).into_owned());
        off = end + 1;
    }
    off += 2; // app id
    let players = *data.get(off)? as u32;
    let max_players = *data.get(off + 1)? as u32;
    // after: bots, server type, environment, visibility, VAC, then the version string
    let vstart = off + 7;
    let version = data.get(vstart..).and_then(|rest| rest.iter().position(|&b| b == 0).map(|e| String::from_utf8_lossy(&rest[..e]).into_owned()));
    Some(ServerQuery { name: strings[0].clone(), players, max_players, version: version.filter(|v| !v.is_empty()), ping_ms: None })
}

pub fn query(address: &str, timeout: std::time::Duration) -> Result<ServerQuery> {
    let addr = address
        .to_socket_addrs()
        .map_err(|e| Error::invalid(format!("{address}: {e}")))?
        .next()
        .ok_or_else(|| Error::invalid(format!("{address}: no address")))?;
    let s = UdpSocket::bind(if addr.is_ipv4() { "0.0.0.0:0" } else { "[::]:0" })?;
    s.set_read_timeout(Some(timeout.max(Duration::from_millis(50))))?;
    let net = |e: std::io::Error| Error::new(ErrorCode::Network, format!("A2S {address}: {e}"));
    let t0 = Instant::now();
    s.send_to(REQUEST, addr).map_err(net)?;
    let mut buf = [0u8; 4096];
    let (mut n, _) = s.recv_from(&mut buf).map_err(net)?;
    if n >= 9 && &buf[..5] == b"\xff\xff\xff\xffA" {
        let mut req = REQUEST.to_vec();
        req.extend_from_slice(&buf[5..9]);
        s.send_to(&req, addr).map_err(net)?;
        n = s.recv_from(&mut buf).map_err(net)?.0;
    }
    let mut q = parse_info(&buf[..n]).ok_or_else(|| Error::new(ErrorCode::Parse, format!("A2S {address}: not an info answer")))?;
    q.ping_ms = Some(t0.elapsed().as_millis() as u32);
    Ok(q)
}

#[cfg(test)]
mod tests {
    #[test]
    fn parses_info() {
        let mut d = b"\xff\xff\xff\xffI\x11Ember Isles\0map\0enshrouded\0Enshrouded\0".to_vec();
        d.extend_from_slice(&[0xc1, 0x52, 2, 16, 0, b'd', b'w', 0, 0]);
        d.extend_from_slice(b"0.9.1\0");
        let q = super::parse_info(&d).unwrap();
        assert_eq!((q.name.as_str(), q.players, q.max_players, q.version.as_deref()), ("Ember Isles", 2, 16, Some("0.9.1")));
        assert!(super::parse_info(b"junk").is_none());
    }
}
