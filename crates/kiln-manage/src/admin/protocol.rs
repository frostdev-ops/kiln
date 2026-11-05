//! KAP/1 wire format: `u32 LE length` + one UTF-8 JSON object (a [`Frame`], tagged by "t"). Handshake:
//! `hello{nonce}` -> `auth{cnonce, mac}` -> `ready{mac, ops, topics}` with
//! `mac  = HMAC-SHA256(token, "kap1-auth\n"  + nonce + "\n" + cnonce)` (client) and
//! `smac = HMAC-SHA256(token, "kap1-ready\n" + nonce + "\n" + cnonce)` (server), as lower-case hex. Test vector:
//! schema/fixtures/kap/handshake.json.

use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};

pub const PROTOCOL: &str = "kap/1";
/// Largest frame payload a server sends (bytes); `ready.limits.maxFrame` says what a server uses.
pub const MAX_SERVER_FRAME: u32 = 16 * 1024 * 1024;
/// Largest frame payload a client may send.
pub const MAX_CLIENT_FRAME: u32 = 1024 * 1024;
pub const MAX_CONNECTIONS: u32 = 4;
pub const MAX_INFLIGHT: u32 = 64;
/// Either side pings after this long without sending anything.
pub const PING_INTERVAL_S: u64 = 15;
/// A peer silent for this long is gone.
pub const IDLE_TIMEOUT_S: u64 = 45;
/// The handshake must finish within this.
pub const HANDSHAKE_TIMEOUT_S: u64 = 5;
pub const AUTH_LABEL: &str = "kap1-auth";
pub const READY_LABEL: &str = "kap1-ready";
pub const TOKEN_FILE: &str = "admin-token";
pub const ENDPOINT_FILE: &str = "endpoint.json";

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ServerIdent {
    #[serde(default)]
    pub id: Option<String>,
    #[serde(default)]
    pub name: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Limits {
    pub max_frame: u32,
    pub max_client_frame: u32,
    pub inflight: u32,
    pub ping_s: u64,
    pub idle_s: u64,
}

impl Default for Limits {
    fn default() -> Self {
        Limits { max_frame: MAX_SERVER_FRAME, max_client_frame: MAX_CLIENT_FRAME, inflight: MAX_INFLIGHT, ping_s: PING_INTERVAL_S, idle_s: IDLE_TIMEOUT_S }
    }
}

/// One KAP/1 frame.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "t", rename_all = "lowercase")]
pub enum Frame {
    /// server -> client, first frame.
    Hello {
        proto: String,
        /// 32 random bytes, 64 lower-case hex chars.
        nonce: String,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        kiln: Option<String>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        admin: Option<String>,
        #[serde(default)]
        server: ServerIdent,
        /// Offered methods: ["hmac-sha256"].
        auth: Vec<String>,
    },
    /// client -> server.
    Auth {
        proto: String,
        cnonce: String,
        mac: String,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        client: Option<String>,
    },
    /// server -> client: authenticated.
    Ready {
        session: String,
        /// The server's proof (smac).
        mac: String,
        ops: Vec<String>,
        topics: Vec<String>,
        #[serde(default)]
        limits: Limits,
    },
    /// client -> server: run an op; `args` is the file-bridge request object without "op".
    Req {
        id: u64,
        op: String,
        #[serde(default)]
        args: serde_json::Map<String, serde_json::Value>,
    },
    /// server -> client: an op's result (the file bridge's result object plus `id`; `code` on failure).
    Res {
        id: u64,
        ok: bool,
        op: String,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        ms: Option<u64>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        data: Option<serde_json::Value>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        error: Option<String>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        code: Option<KapErrorCode>,
    },
    /// client -> server: start pushes of a topic (answered by a `res` with op "sub").
    Sub {
        id: u64,
        topic: String,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        every_ms: Option<u64>,
    },
    Unsub {
        id: u64,
        topic: String,
    },
    /// server -> client: topic data.
    Push {
        topic: String,
        seq: u64,
        data: serde_json::Value,
    },
    Ping {
        id: u64,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        ts: Option<u64>,
    },
    Pong {
        id: u64,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        ts: Option<u64>,
    },
    /// A protocol error (not an op failure). Fatal ones are followed by close.
    Error {
        code: KapErrorCode,
        message: String,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        id: Option<u64>,
    },
    /// Either side, before closing.
    Bye {
        reason: String,
    },
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum KapErrorCode {
    BadFrame,
    TooLarge,
    AuthRequired,
    AuthFailed,
    Version,
    UnknownOp,
    OpFailed,
    Busy,
    UnknownTopic,
    Timeout,
    ShuttingDown,
    Internal,
}

/// `kiln/data/kiln-admin/endpoint.json` (schema/admin-endpoint.schema.json).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Endpoint {
    pub format: u32,
    pub protocol: String,
    pub host: String,
    pub port: u16,
    pub pid: u32,
    pub started: String,
    #[serde(default)]
    pub kiln: Option<String>,
    #[serde(default)]
    pub admin: Option<String>,
    #[serde(default)]
    pub server: ServerIdent,
    #[serde(default)]
    pub token: Option<String>,
    #[serde(default)]
    pub bridge: Option<String>,
}

/// Topics a Kiln.Admin server may offer (`ready.topics` says which it does).
pub const TOPICS: &[&str] = &["state", "log"];

/// How long a server takes at most to answer `op` (admin-protocol.md section 4).
pub fn op_budget(op: &str) -> std::time::Duration {
    let s = match op {
        "mod_test" => 180,
        "backup_create" => 90,
        "save_now" => 60,
        o if o.starts_with("mod_") => 45,
        _ => 30,
    };
    std::time::Duration::from_secs(s)
}

/// Writes one frame (`u32 LE length` + JSON).
pub fn write_frame(w: &mut impl std::io::Write, frame: &Frame) -> Result<()> {
    let bytes = encode(frame)?;
    w.write_all(&bytes).map_err(|e| Error::new(ErrorCode::Network, format!("KAP send: {e}")))?;
    Ok(())
}

/// Reads one frame payload (without the length) of at most `max` bytes. Ok(None) at a clean end of stream.
pub fn read_payload(r: &mut impl std::io::Read, max: u32) -> Result<Option<Vec<u8>>> {
    let mut head = [0u8; 4];
    match r.read_exact(&mut head) {
        Ok(()) => {}
        Err(e) if e.kind() == std::io::ErrorKind::UnexpectedEof => return Ok(None),
        Err(e) => return Err(Error::new(ErrorCode::Network, format!("KAP receive: {e}"))),
    }
    let len = u32::from_le_bytes(head);
    if len == 0 {
        return Err(Error::new(ErrorCode::Parse, "KAP: zero-length frame"));
    }
    if len > max {
        return Err(Error::new(ErrorCode::Parse, format!("KAP: a {len}-byte frame is over the {max}-byte limit")));
    }
    let mut buf = vec![0u8; len as usize];
    r.read_exact(&mut buf).map_err(|e| Error::new(ErrorCode::Network, format!("KAP receive: {e}")))?;
    Ok(Some(buf))
}

/// Decodes a payload; Ok(None) for a frame type this crate does not know (KAP/1 peers ignore those).
pub fn decode_known(payload: &[u8]) -> Result<Option<Frame>> {
    let v: serde_json::Value = serde_json::from_slice(payload)?;
    let known = matches!(
        v.get("t").and_then(|t| t.as_str()),
        Some("hello" | "auth" | "ready" | "req" | "res" | "sub" | "unsub" | "push" | "ping" | "pong" | "error" | "bye")
    );
    if !known {
        return Ok(None);
    }
    Ok(Some(serde_json::from_value(v)?))
}

/// A KAP error code as the crate's error code (for `res{ok: false, code}` and fatal `error` frames).
pub fn error_code(code: Option<KapErrorCode>) -> ErrorCode {
    match code {
        None | Some(KapErrorCode::OpFailed) | Some(KapErrorCode::UnknownOp) | Some(KapErrorCode::TooLarge) => ErrorCode::OpFailed,
        Some(KapErrorCode::Busy) => ErrorCode::Busy,
        Some(KapErrorCode::Timeout) => ErrorCode::Timeout,
        Some(KapErrorCode::AuthFailed) | Some(KapErrorCode::AuthRequired) => ErrorCode::AuthFailed,
        Some(KapErrorCode::ShuttingDown) => ErrorCode::AdminUnavailable,
        Some(KapErrorCode::UnknownTopic) | Some(KapErrorCode::BadFrame) => ErrorCode::InvalidArgument,
        Some(KapErrorCode::Version) => ErrorCode::Unsupported,
        Some(KapErrorCode::Internal) => ErrorCode::Internal,
    }
}

/// `u32 LE length` + JSON.
pub fn encode(frame: &Frame) -> Result<Vec<u8>> {
    let json = serde_json::to_vec(frame)?;
    let len = u32::try_from(json.len()).map_err(|_| Error::new(ErrorCode::InvalidArgument, "frame too large"))?;
    let mut out = Vec::with_capacity(json.len() + 4);
    out.extend_from_slice(&len.to_le_bytes());
    out.extend_from_slice(&json);
    Ok(out)
}

/// Decodes one payload (without the length prefix).
pub fn decode(payload: &[u8]) -> Result<Frame> {
    Ok(serde_json::from_slice(payload)?)
}

/// The admin-token file's content: 64 hex chars (whitespace around them ignored) -> 32 bytes.
pub fn parse_token(text: &str) -> Result<[u8; 32]> {
    let t = text.trim();
    if t.len() != 64 {
        return Err(Error::new(ErrorCode::Parse, "admin-token must hold 64 hex characters"));
    }
    let mut out = [0u8; 32];
    for (i, b) in out.iter_mut().enumerate() {
        *b = u8::from_str_radix(&t[i * 2..i * 2 + 2], 16).map_err(|_| Error::new(ErrorCode::Parse, "admin-token is not hex"))?;
    }
    Ok(out)
}

/// `label + "\n" + nonce + "\n" + cnonce`
pub fn mac_message(label: &str, nonce: &str, cnonce: &str) -> String {
    format!("{label}\n{nonce}\n{cnonce}")
}

/// HMAC-SHA256 as lower-case hex (feature `admin`).
#[cfg(feature = "admin")]
pub fn mac(token: &[u8; 32], label: &str, nonce: &str, cnonce: &str) -> String {
    use hmac::{Hmac, KeyInit, Mac};
    let mut m = <Hmac<sha2::Sha256> as KeyInit>::new_from_slice(token).expect("HMAC takes any key length");
    m.update(mac_message(label, nonce, cnonce).as_bytes());
    crate::hash::hex(m.finalize().into_bytes().as_slice())
}

#[cfg(not(feature = "admin"))]
pub fn mac(token: &[u8; 32], label: &str, nonce: &str, cnonce: &str) -> String {
    let _ = (token, label, nonce, cnonce);
    todo!("kiln-manage feature \"admin\"")
}
