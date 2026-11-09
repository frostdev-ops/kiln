//! Nexus public-client OAuth authorization code flow with PKCE (no client secret).
//! Provider: https://modding.wiki/en/api/oauth2-guide and the provider's OpenID discovery document.
//! Account data is validated by Nexus's authenticated account endpoint, never by trusting an unverified JWT body.

use super::api::{Credentials, HttpRequest, Transport};
use super::keystore::{TokenPlace, TokenStore};
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::io::{Read, Write};
use std::net::{SocketAddrV4, TcpListener};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

pub const ORIGIN: &str = "https://users.nexusmods.com";
pub const TOKEN_URL: &str = "https://users.nexusmods.com/oauth/token";
const LOGIN_TIMEOUT: Duration = Duration::from_secs(180);
mod registration;

#[derive(Clone, Debug)]
pub struct Config {
    pub client_id: String,
    pub redirect_uri: String,
    address: SocketAddrV4,
    path: String,
}

impl Config {
    /// Both values must come from Kiln's own approved public-client registration.
    pub fn registered() -> Result<Option<Self>> {
        registration::preflight(false, option_env!("KILN_NEXUS_OAUTH_CLIENT_ID"), option_env!("KILN_NEXUS_OAUTH_REDIRECT_URI")).map_err(Error::invalid)?;
        match (option_env!("KILN_NEXUS_OAUTH_CLIENT_ID"), option_env!("KILN_NEXUS_OAUTH_REDIRECT_URI")) {
            (Some(id), Some(uri)) if !id.is_empty() && !uri.is_empty() => Self::new(id, uri).map(Some),
            _ => Ok(None),
        }
    }

    pub fn new(client_id: &str, redirect_uri: &str) -> Result<Self> {
        let (address, path) = registration::validate(client_id, redirect_uri).map_err(Error::invalid)?;
        Ok(Self { client_id: client_id.into(), redirect_uri: redirect_uri.into(), address, path })
    }
}

/// Persisted only in the private token store. Debug must never reveal token material.
#[derive(Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Tokens {
    pub client_id: String,
    pub access_token: String,
    pub refresh_token: String,
    pub expires_at: i64,
}
impl std::fmt::Debug for Tokens {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Tokens").field("client_id", &self.client_id).field("credentials", &"<hidden>").field("expires_at", &self.expires_at).finish()
    }
}
impl Tokens {
    pub fn validate(&self) -> Result<()> {
        let valid = |s: &str| !s.is_empty() && s.len() <= 16 * 1024 && !s.chars().any(|c| c.is_whitespace() || c.is_control());
        if self.client_id.is_empty() || !valid(&self.access_token) || !valid(&self.refresh_token) || self.expires_at <= 0 {
            return Err(Error::new(ErrorCode::Parse, "Nexus Mods returned invalid OAuth credentials."));
        }
        Ok(())
    }
}

fn encode(s: &str) -> String {
    let mut out = String::new();
    for b in s.bytes() {
        if b.is_ascii_alphanumeric() || b"-._~".contains(&b) { out.push(char::from(b)); }
        else { use std::fmt::Write; let _ = write!(&mut out, "%{b:02X}"); }
    }
    out
}
fn form(fields: &[(&str, &str)]) -> String { fields.iter().map(|(k,v)| format!("{}={}", encode(k), encode(v))).collect::<Vec<_>>().join("&") }
fn decode(s: &str) -> Result<String> {
    let bytes = s.as_bytes(); let mut out = Vec::new(); let mut i = 0;
    while i < bytes.len() {
        match bytes[i] {
            b'%' if i + 2 < bytes.len() => {
                let a = (bytes[i+1] as char).to_digit(16); let b = (bytes[i+2] as char).to_digit(16);
                out.push(a.zip(b).map(|(a,b)| (a*16+b) as u8).ok_or_else(|| Error::invalid("Invalid OAuth callback."))?); i += 3;
            }
            b'%' => return Err(Error::invalid("Invalid OAuth callback.")),
            b'+' => { out.push(b' '); i += 1; }
            b => { out.push(b); i += 1; }
        }
    }
    String::from_utf8(out).map_err(|_| Error::invalid("Invalid OAuth callback."))
}
fn challenge(verifier: &str) -> String {
    const ALPHABET: &[u8] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
    let hash = Sha256::digest(verifier.as_bytes()); let mut out = String::new();
    for chunk in hash.chunks(3) {
        let bits = (u32::from(chunk[0]) << 16) | (u32::from(*chunk.get(1).unwrap_or(&0)) << 8) | u32::from(*chunk.get(2).unwrap_or(&0));
        for n in 0..chunk.len()+1 { out.push(char::from(ALPHABET[((bits >> (18-6*n)) & 63) as usize])); }
    }
    out
}

/// An ephemeral verifier/state and bound listener live only for this sign-in attempt.
pub struct Login {
    config: Config,
    verifier: String,
    state: String,
    listener: TcpListener,
}
impl Login {
    pub fn bind(config: Config) -> Result<Self> {
        let listener = TcpListener::bind(config.address).map_err(|_| Error::new(ErrorCode::Busy, "The Nexus sign-in callback port is in use. Close the other sign-in attempt and try again."))?;
        listener.set_nonblocking(true)?;
        Ok(Self { config, verifier: crate::hash::hex(&crate::util::random_bytes::<32>()?), state: crate::hash::hex(&crate::util::random_bytes::<32>()?), listener })
    }
    pub fn authorize_url(&self) -> String {
        format!("{ORIGIN}/oauth/authorize?{}", form(&[("client_id", &self.config.client_id), ("response_type", "code"), ("scope", ""),
            ("redirect_uri", &self.config.redirect_uri), ("state", &self.state), ("code_challenge_method", "S256"), ("code_challenge", &challenge(&self.verifier))]))
    }
    fn callback(&self, target: &str) -> Result<Option<String>> {
        let (path, query) = target.split_once('?').unwrap_or((target, ""));
        if path != self.config.path { return Ok(None); }
        let mut fields = std::collections::BTreeMap::new();
        for pair in query.split('&').filter(|p| !p.is_empty()) {
            let (k,v) = pair.split_once('=').ok_or_else(|| Error::invalid("Invalid OAuth callback."))?;
            if fields.insert(decode(k)?, decode(v)?).is_some() { return Err(Error::invalid("Duplicate OAuth callback parameter.")); }
        }
        if fields.get("state") != Some(&self.state) { return Err(Error::new(ErrorCode::Refused, "The Nexus sign-in callback did not match this attempt.")); }
        if fields.contains_key("error") { return Err(Error::new(ErrorCode::Canceled, "Nexus Mods sign-in was not authorized.")); }
        let code = fields.remove("code").filter(|s| !s.is_empty() && s.len() <= 4096 && !s.chars().any(char::is_control))
            .ok_or_else(|| Error::invalid("Nexus Mods returned no authorization code."))?;
        Ok(Some(code))
    }
    pub fn wait(self, transport: &dyn Transport, canceled: impl Fn() -> bool) -> Result<Tokens> {
        self.wait_timeout(transport, canceled, LOGIN_TIMEOUT)
    }
    fn wait_timeout(self, transport: &dyn Transport, canceled: impl Fn() -> bool, timeout: Duration) -> Result<Tokens> {
        let started = Instant::now();
        loop {
            if canceled() { return Err(Error::new(ErrorCode::Canceled, "Nexus Mods sign-in canceled.")); }
            if started.elapsed() >= timeout { return Err(Error::new(ErrorCode::Timeout, "Nexus Mods sign-in timed out. Try again.")); }
            match self.listener.accept() {
                Ok((mut stream, peer)) => {
                    if !peer.ip().is_loopback() { continue; }
                    stream.set_read_timeout(Some(Duration::from_millis(300)))?;
                    stream.set_write_timeout(Some(Duration::from_millis(300)))?;
                    let connection_started = Instant::now();
                    let mut bytes = Vec::new(); let mut buffer = [0; 1024];
                    while bytes.len() < 8192 && connection_started.elapsed() < Duration::from_secs(2)
                        && started.elapsed() < timeout && !canceled() && !bytes.windows(4).any(|x| x == b"\r\n\r\n") {
                        match stream.read(&mut buffer[..]) { Ok(0) | Err(_) => break, Ok(n) => bytes.extend_from_slice(&buffer[..n]) }
                    }
                    let request = std::str::from_utf8(&bytes).ok().filter(|_| bytes.len() <= 8192 && bytes.ends_with(b"\r\n\r\n"));
                    let expected_host = self.config.address.to_string();
                    let request = request.filter(|s| {
                        let hosts: Vec<_> = s.lines().skip(1).filter_map(|line| line.split_once(':'))
                            .filter(|(name,_)| name.eq_ignore_ascii_case("host")).collect();
                        hosts.len() == 1 && hosts[0].1.trim() == expected_host
                    });
                    let callback = request.and_then(|s| s.lines().next()).and_then(|s| s.strip_prefix("GET ")).and_then(|s| s.strip_suffix(" HTTP/1.1"))
                        .filter(|target| target.starts_with('/') && !target.starts_with("//"));
                    let result = callback.map(|t| self.callback(t)).unwrap_or_else(|| Err(Error::invalid("Invalid OAuth callback request.")));
                    let (status, text) = if matches!(result, Ok(Some(_))) { ("200 OK", "Authorization received. Return to Kiln.") } else { ("400 Bad Request", "This request could not complete the sign-in. Return to Kiln.") };
                    let _ = write!(stream, "HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'\r\nConnection: close\r\nContent-Length: {}\r\n\r\n{text}", text.len());
                    match result {
                        Ok(Some(code)) => {
                            if canceled() { return Err(Error::new(ErrorCode::Canceled, "Nexus Mods sign-in canceled.")); }
                            return token_request(transport, &self.config, &[("grant_type", "authorization_code"), ("client_id", &self.config.client_id),
                                ("redirect_uri", &self.config.redirect_uri), ("scope", ""), ("code", &code), ("code_verifier", &self.verifier)]);
                        }
                        Err(e) if e.code == ErrorCode::Canceled => return Err(e),
                        _ => {}, // An unrelated or mismatched request cannot consume the live attempt.
                    }
                }
                Err(e) if e.kind() == std::io::ErrorKind::WouldBlock => std::thread::sleep(Duration::from_millis(50)),
                Err(_) => return Err(Error::new(ErrorCode::Io, "The Nexus sign-in callback listener failed.")),
            }
        }
    }
}

fn token_request(transport: &dyn Transport, config: &Config, fields: &[(&str, &str)]) -> Result<Tokens> {
    let response = transport.send(&HttpRequest { method: "POST", url: TOKEN_URL.into(),
        headers: vec![("Content-Type".into(), "application/x-www-form-urlencoded".into()), ("Accept".into(), "application/json".into())], body: Some(form(fields).into_bytes()) })?;
    if response.status == 429 { return Err(Error::new(ErrorCode::Busy, "Nexus Mods asked Kiln to wait before signing in again.")); }
    if (400..500).contains(&response.status) { return Err(Error::new(ErrorCode::AuthFailed, "Nexus Mods authorization expired or was revoked. Sign in again.")); }
    if response.status != 200 { return Err(Error::new(ErrorCode::Network, "Nexus Mods could not complete authorization. Try again.")); }
    #[derive(Deserialize)]
    struct Reply { access_token: String, refresh_token: String, expires_in: i64, token_type: String }
    let reply: Reply = serde_json::from_slice(&response.body).map_err(|_| Error::new(ErrorCode::Parse, "Nexus Mods returned an invalid OAuth response."))?;
    if !reply.token_type.eq_ignore_ascii_case("bearer") || reply.expires_in <= 0 || reply.expires_in > 31 * 86400 {
        return Err(Error::new(ErrorCode::Parse, "Nexus Mods returned an invalid OAuth lifetime or token type."));
    }
    let tokens = Tokens { client_id: config.client_id.clone(), access_token: reply.access_token, refresh_token: reply.refresh_token, expires_at: crate::util::now_unix() + reply.expires_in };
    tokens.validate()?;
    Ok(tokens)
}

/// One mutex serializes refresh, persistence and logout so stale refresh results cannot resurrect a signed-out session.
pub struct Session {
    config: Config,
    transport: Arc<dyn Transport>,
    store: TokenStore,
    tokens: Mutex<Option<Tokens>>,
}
impl Session {
    pub fn new(config: Config, transport: Arc<dyn Transport>, store: TokenStore) -> Result<Self> {
        let _lock = store.lock()?;
        let tokens = store.get()?.map(|(t,_)| t).filter(|t| t.client_id == config.client_id);
        drop(_lock);
        Ok(Self { config, transport, store, tokens: Mutex::new(tokens) })
    }
    pub fn save(&self, tokens: Tokens) -> Result<TokenPlace> {
        tokens.validate()?;
        if tokens.client_id != self.config.client_id { return Err(Error::new(ErrorCode::Refused, "OAuth credentials belong to a different application registration.")); }
        let mut state = self.tokens.lock().unwrap();
        let _lock = self.store.lock()?;
        let place = match self.store.set(&tokens) {
            Ok(place) => place,
            Err(error) => {
                // A backend may write successfully before its verification or file cleanup fails.
                *state = None;
                if let Err(cleanup) = self.store.clear_locked() {
                    return Err(Error::new(error.code, format!("{}; {}", error.message, cleanup.message)));
                }
                return Err(error);
            }
        };
        *state = Some(tokens);
        Ok(place)
    }
    pub fn clear(&self) -> Result<()> {
        let mut state = self.tokens.lock().unwrap();
        let _lock = self.store.lock()?;
        *state = None;
        self.store.clear_locked()
    }
    pub fn place(&self) -> Result<Option<TokenPlace>> {
        let mut state = self.tokens.lock().unwrap();
        let _lock = self.store.lock()?;
        let stored = self.store.get()?.filter(|(t,_)| t.client_id == self.config.client_id);
        *state = stored.as_ref().map(|(t,_)| t.clone());
        Ok(stored.map(|(_, place)| place))
    }
    fn refresh(&self, rejected: Option<&str>) -> Result<Option<String>> {
        let mut state = self.tokens.lock().unwrap();
        let _lock = self.store.lock()?;
        *state = self.store.get()?.map(|(t,_)| t).filter(|t| t.client_id == self.config.client_id);
        let Some(tokens) = state.as_ref() else { return Ok(None) };
        if tokens.expires_at > crate::util::now_unix() + 30 && rejected != Some(tokens.access_token.as_str()) { return Ok(Some(tokens.access_token.clone())); }
        let result = token_request(self.transport.as_ref(), &self.config, &[("grant_type", "refresh_token"), ("client_id", &self.config.client_id), ("refresh_token", &tokens.refresh_token)]);
        match result {
            Ok(tokens) => {
                if let Err(error) = self.store.set(&tokens) {
                    *state = None;
                    if let Err(cleanup) = self.store.clear_locked() {
                        return Err(Error::new(error.code, format!("{}; {}", error.message, cleanup.message)));
                    }
                    return Err(error);
                }
                let access = tokens.access_token.clone(); *state = Some(tokens); Ok(Some(access))
            }
            Err(e) if e.code == ErrorCode::AuthFailed => {
                *state = None; self.store.clear_locked()?; Err(e)
            }
            Err(e) => Err(e),
        }
    }
}
impl Credentials for Session {
    fn present(&self) -> bool { self.tokens.lock().unwrap().is_some() }
    fn access_token(&self) -> Result<Option<String>> { self.refresh(None) }
    fn refresh_rejected(&self, token: &str) -> Result<Option<String>> { self.refresh(Some(token)) }
    fn forget_rejected(&self, rejected: &str) -> Result<()> {
        let mut state = self.tokens.lock().unwrap();
        let _lock = self.store.lock()?;
        *state = self.store.get()?.map(|(tokens,_)| tokens).filter(|tokens| tokens.client_id == self.config.client_id);
        // An in-flight request can fail after browser reconnect saved a newer account.
        if state.as_ref().is_some_and(|tokens| tokens.access_token == rejected) {
            *state = None;
            self.store.clear_locked()?;
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests;
