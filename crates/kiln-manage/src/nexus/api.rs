//! The Nexus Mods API client: v3 (`https://api.nexusmods.com/v3`, OpenAPI at api.nexusmods.com/openapi.yaml) for mods,
//! files, versions, dependencies and download links; v1 `users/validate.json` only for the account's name and premium
//! state (v3 has no user endpoint).
//!
//! Nexus's rules for apps, as this client follows them:
//! - Every request names the app: `Application-Name` + `Application-Version` and a `User-Agent` of the same; nothing
//!   personal (no user name, no machine name).
//! - The OAuth access token goes only to the API host (`Authorization: Bearer` header), never to the CDN or the archive manifests, and is
//!   never logged, printed or put into an error message ([`Client`]'s `Debug` hides it).
//! - Rate limits: one request at a time per client, at least [`MIN_GAP`] apart; the `x-rl-*-remaining` headers are
//!   read and requests stop before the hourly or daily budget runs out; a 429 blocks the client for its `Retry-After`.
//! - Downloads: premium users get links without a key; everyone else needs the `key` + `expires` pair the website
//!   puts in a "Mod Manager Download" (nxm://) link. Free users are never sent around that (see catalog.rs).
//!
//! The HTTP layer is a [`Transport`], so tests run the whole client against a mock (tests.rs).

use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::io::Read;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

/// The API host. Access tokens are sent to this origin only.
pub const API_ORIGIN: &str = "https://api.nexusmods.com";
/// The smallest gap between two API requests of one client.
pub const MIN_GAP: Duration = Duration::from_millis(150);
/// Requests stop when fewer than this many remain in the hour (or the day), so other apps of the user keep some.
pub const RESERVE: i64 = 20;

// ---------------------------------------------------------------- transport

#[derive(Clone)]
pub struct HttpRequest {
    pub method: &'static str,
    pub url: String,
    pub headers: Vec<(String, String)>,
    pub body: Option<Vec<u8>>,
}

impl std::fmt::Debug for HttpRequest {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        // Both the API header and signed download URLs carry credentials. Bodies and response text can echo them.
        f.debug_struct("HttpRequest").field("method", &self.method)
            .field("url", &self.url.split(['?', '#']).next().unwrap_or("<hidden>"))
            .field("headers", &"<hidden>").field("body", &"<hidden>").finish()
    }
}

#[derive(Clone, Default)]
pub struct HttpResponse {
    pub status: u16,
    pub headers: Vec<(String, String)>,
    pub body: Vec<u8>,
}

impl std::fmt::Debug for HttpResponse {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("HttpResponse").field("status", &self.status).field("headers", &"<hidden>")
            .field("body", &"<hidden>").finish()
    }
}

impl HttpResponse {
    pub fn header(&self, name: &str) -> Option<&str> {
        self.headers.iter().find(|(k, _)| k.eq_ignore_ascii_case(name)).map(|(_, v)| v.as_str())
    }
}

/// A streamed body (downloads).
pub struct HttpStream {
    pub status: u16,
    pub length: Option<u64>,
    pub reader: Box<dyn Read + Send>,
}

/// The HTTP layer: ureq in the app, a mock in tests.
pub trait Transport: Send + Sync {
    fn send(&self, req: &HttpRequest) -> Result<HttpResponse>;
    /// GET with a streamed body (no authorization is ever passed here: the URLs are the CDN's).
    fn open(&self, url: &str, headers: &[(String, String)]) -> Result<HttpStream>;
}

/// The real transport (feature `http`): https only, timeouts, the system proxy, at most 5 redirects.
#[cfg(feature = "http")]
pub struct UreqTransport {
    agent: ureq::Agent,
}

#[cfg(feature = "http")]
impl UreqTransport {
    pub fn new(user_agent: &str) -> UreqTransport {
        let agent: ureq::Agent = ureq::Agent::config_builder()
            .https_only(true)
            .http_status_as_error(false)
            .timeout_connect(Some(Duration::from_secs(20)))
            .timeout_recv_response(Some(Duration::from_secs(60)))
            .timeout_per_read(Some(Duration::from_secs(60)))
            .max_redirects(5)
            .user_agent(user_agent)
            .build()
            .into();
        UreqTransport { agent }
    }
}

#[cfg(feature = "http")]
fn net(_url: &str, _e: impl std::fmt::Display) -> Error {
    // Transport diagnostics may contain the full signed URL or a rejected secret header value.
    Error::new(ErrorCode::Network, "The Nexus Mods network request failed. Check your connection and try again.")
}

#[cfg_attr(not(feature = "http"), allow(dead_code))]
fn api_url(url: &str) -> bool {
    url.strip_prefix(API_ORIGIN).is_some_and(|rest| rest.starts_with('/'))
}

#[cfg(feature = "http")]
impl Transport for UreqTransport {
    fn send(&self, req: &HttpRequest) -> Result<HttpResponse> {
        super::require_enabled()?;
        if req.headers.iter().any(|(k, _)| k.eq_ignore_ascii_case("apikey")) {
            return Err(Error::new(ErrorCode::Refused, "Personal API key authentication is not supported."));
        }
        let credentialed = req.headers.iter().any(|(k, _)| k.eq_ignore_ascii_case("authorization"));
        let oauth = req.url == super::oauth::TOKEN_URL;
        if credentialed && !api_url(&req.url) {
            return Err(Error::new(ErrorCode::Refused, "Access tokens may only be sent to the Nexus Mods API."));
        }
        // Neither access-token headers nor OAuth form bodies may follow redirects.
        let redirects = if credentialed || oauth { 0 } else { 5 };
        let mut resp = match req.method {
            "POST" => {
                let mut b = self.agent.post(&req.url).config().max_redirects(redirects).build();
                for (k, v) in &req.headers {
                    b = b.header(k.as_str(), v.as_str());
                }
                b.send(req.body.as_deref().unwrap_or_default()).map_err(|e| net(&req.url, e))?
            }
            _ => {
                let mut b = self.agent.get(&req.url).config().max_redirects(redirects).build();
                for (k, v) in &req.headers {
                    b = b.header(k.as_str(), v.as_str());
                }
                b.call().map_err(|e| net(&req.url, e))?
            }
        };
        let status = resp.status().as_u16();
        let headers = resp.headers().iter().map(|(k, v)| (k.as_str().to_string(), v.to_str().unwrap_or("").to_string())).collect();
        let body = resp.body_mut().with_config().limit(if oauth { 64 * 1024 } else { 32 * 1024 * 1024 }).read_to_vec().map_err(|e| net(&req.url, e))?;
        Ok(HttpResponse { status, headers, body })
    }

    fn open(&self, url: &str, headers: &[(String, String)]) -> Result<HttpStream> {
        super::require_enabled()?;
        if headers.iter().any(|(k, _)| k.eq_ignore_ascii_case("apikey") || k.eq_ignore_ascii_case("authorization")) {
            return Err(Error::new(ErrorCode::Refused, "Downloads must not carry account credentials."));
        }
        let mut b = self.agent.get(url);
        for (k, v) in headers {
            b = b.header(k.as_str(), v.as_str());
        }
        let resp = b.call().map_err(|e| net(url, e))?;
        let status = resp.status().as_u16();
        let length = resp.body().content_length();
        let reader = resp.into_body().into_with_config().limit(super::MAX_DOWNLOAD + 1).reader();
        Ok(HttpStream { status, length, reader: Box::new(reader) })
    }
}

// ---------------------------------------------------------------- API shapes (the parts Kiln reads)

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct IdName {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: Option<String>,
}

/// A mod in search results (`ModSummary`).
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct ModSummary {
    /// Composite id (gameId << 32 | modId).
    pub id: String,
    pub game_scoped_id: String,
    pub name: String,
    #[serde(default)]
    pub summary: Option<String>,
    #[serde(default)]
    pub version: Option<String>,
    #[serde(default)]
    pub author: Option<IdName>,
    #[serde(default)]
    pub category: Option<IdName>,
    #[serde(default)]
    pub thumbnail_url: Option<String>,
    #[serde(default)]
    pub endorsements: u64,
    #[serde(default)]
    pub downloads: u64,
    #[serde(default)]
    pub unique_downloads: u64,
    #[serde(default)]
    pub file_size_kilobytes: u64,
    #[serde(default)]
    pub created_at: String,
    #[serde(default)]
    pub updated_at: String,
    #[serde(default)]
    pub adult: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct ModImage {
    #[serde(default)]
    pub url: String,
    #[serde(default)]
    pub thumbnail_url: Option<String>,
    #[serde(default)]
    pub large_thumbnail_url: Option<String>,
    #[serde(default)]
    pub title: Option<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct ModStats {
    #[serde(default)]
    pub downloads: u64,
    #[serde(default)]
    pub unique_downloads: u64,
    #[serde(default)]
    pub endorsements: u64,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct MinimalVersion {
    pub id: String,
    #[serde(default)]
    pub game_scoped_id: String,
}

/// A mod's page (`Mod`).
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct ModDetails {
    pub id: String,
    pub game_scoped_id: String,
    pub name: String,
    #[serde(default)]
    pub description: Option<String>,
    #[serde(default)]
    pub version: Option<String>,
    /// Free-text author credit.
    #[serde(default)]
    pub author: Option<String>,
    #[serde(default)]
    pub uploader: Option<IdName>,
    #[serde(default)]
    pub category: Option<IdName>,
    #[serde(default)]
    pub stats: ModStats,
    #[serde(default)]
    pub tags: Vec<IdName>,
    #[serde(default)]
    pub status: Option<String>,
    #[serde(default)]
    pub adult_content: bool,
    #[serde(default)]
    pub primary_image: Option<ModImage>,
    #[serde(default)]
    pub gallery_images: Vec<ModImage>,
    #[serde(default)]
    pub primary_file_version: Option<MinimalVersion>,
}

/// A mod file (the updatable file on a page) with its aggregates.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct ModFile {
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub is_active: bool,
    #[serde(default)]
    pub last_file_uploaded_at: Option<String>,
    #[serde(default)]
    pub versions_count: u64,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct FileRef {
    pub id: String,
    #[serde(default)]
    pub name: String,
}

/// One version of a mod file.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct FileVersion {
    pub id: String,
    #[serde(default)]
    pub file: FileRef,
    /// Decimal string; higher is newer within the file.
    #[serde(default)]
    pub position: String,
    #[serde(default)]
    pub game_scoped_id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub version: String,
    #[serde(default)]
    pub category: String,
    #[serde(default)]
    pub uploaded_at: String,
    #[serde(default)]
    pub manifest_url: Option<String>,
    #[serde(default)]
    pub is_primary: bool,
}

impl FileVersion {
    pub fn position_value(&self) -> f64 {
        self.position.parse().unwrap_or(0.0)
    }
    /// main, update, optional, miscellaneous: what the files tab lists.
    pub fn is_listed(&self) -> bool {
        matches!(self.category.as_str(), "main" | "update" | "optional" | "miscellaneous")
    }
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct MinimalMod {
    pub id: String,
    #[serde(default)]
    pub game_scoped_id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub game: Option<MinimalGame>,
    #[serde(default)]
    pub thumbnail_url: Option<String>,
    #[serde(default)]
    pub status: Option<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct MinimalGame {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub domain_name: String,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct TargetFile {
    pub id: String,
    #[serde(default)]
    pub name: String,
    pub r#mod: MinimalMod,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct DependencyRange {
    pub target_mod_file: TargetFile,
    #[serde(default)]
    pub min_version: Option<FileVersion>,
    #[serde(default)]
    pub max_version: Option<FileVersion>,
}

/// One dependency definition: any of its ranges satisfies it.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct Dependency {
    pub id: String,
    #[serde(default)]
    pub ranges: Vec<DependencyRange>,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct DownloadLink {
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub short_name: String,
    pub url: String,
}

/// The account behind a key (v1 users/validate.json): only the name and the premium flag are kept.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Account {
    pub name: String,
    pub premium: bool,
}

/// One file of an archive as Nexus's manifest lists it.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ManifestEntry {
    /// `/`-separated, no leading `/`.
    pub path: String,
    pub size: Option<u64>,
    /// Lower-case hex, when the manifest has a sha256 for it.
    pub sha256: Option<String>,
}

/// A search page: the mods and the next cursor.
#[derive(Debug, Clone, Default)]
pub struct SearchPage {
    pub mods: Vec<ModSummary>,
    pub next_cursor: Option<String>,
    pub total: Option<u64>,
}

// ---------------------------------------------------------------- the client

/// The app as it names itself to Nexus Mods.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AppId {
    pub name: String,
    pub version: String,
}

impl AppId {
    pub fn kiln_launcher(version: &str) -> AppId {
        AppId { name: "Kiln Launcher".into(), version: version.into() }
    }
    pub fn user_agent(&self) -> String {
        format!("{}/{} ({}; {})", self.name.replace(' ', "-"), self.version, std::env::consts::OS, std::env::consts::ARCH)
    }
}

#[derive(Debug, Default)]
struct Gate {
    last: Option<Instant>,
    blocked_until: Option<Instant>,
    hourly_remaining: Option<i64>,
    daily_remaining: Option<i64>,
    hourly_reset: Option<Instant>,
    daily_reset: Option<Instant>,
    calls: u64,
}

/// The rate budget as last reported (Settings shows it).
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RateState {
    pub hourly_remaining: Option<i64>,
    pub daily_remaining: Option<i64>,
    /// Seconds until a 429's Retry-After ends (0: not blocked).
    pub blocked_for: u64,
    /// API requests this client made.
    pub calls: u64,
}

/// Backend-held bearer credentials. A session refreshes on expiry and serializes token rotation.
pub trait Credentials: Send + Sync {
    fn present(&self) -> bool;
    fn access_token(&self) -> Result<Option<String>>;
    fn refresh_rejected(&self, _token: &str) -> Result<Option<String>> { Ok(None) }
    fn forget_rejected(&self, _token: &str) -> Result<()> { Ok(()) }
}
struct FixedCredentials(Option<String>);
impl Credentials for FixedCredentials {
    fn present(&self) -> bool { self.0.is_some() }
    fn access_token(&self) -> Result<Option<String>> { Ok(self.0.clone()) }
}

pub struct Client {
    origin: String,
    transport: Arc<dyn Transport>,
    credentials: Arc<dyn Credentials>,
    app: AppId,
    gate: Mutex<Gate>,
    request_lock: Mutex<()>,
    gap: Duration,
}

impl std::fmt::Debug for Client {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Client").field("origin", &self.origin).field("authenticated", &self.has_credentials()).field("app", &self.app).finish()
    }
}

impl Client {
    pub fn new(transport: Arc<dyn Transport>, token: Option<String>, app: AppId) -> Client {
        Self::build(API_ORIGIN, transport, Arc::new(FixedCredentials(token)), app)
    }

    /// Tests point the client at a mock origin.
    #[cfg(test)]
    pub fn with_origin(origin: &str, transport: Arc<dyn Transport>, token: Option<String>, app: AppId) -> Client {
        Self::build(origin, transport, Arc::new(FixedCredentials(token)), app)
    }

    pub fn with_credentials(transport: Arc<dyn Transport>, credentials: Arc<dyn Credentials>, app: AppId) -> Client {
        Self::build(API_ORIGIN, transport, credentials, app)
    }

    fn build(origin: &str, transport: Arc<dyn Transport>, credentials: Arc<dyn Credentials>, app: AppId) -> Client {
        Client { origin: origin.trim_end_matches('/').to_string(), transport, credentials, app, gate: Mutex::new(Gate::default()), request_lock: Mutex::new(()), gap: MIN_GAP }
    }

    /// Tests: no gap between requests.
    pub fn without_gap(mut self) -> Client {
        self.gap = Duration::ZERO;
        self
    }

    pub fn has_credentials(&self) -> bool {
        super::enabled() && self.credentials.present()
    }

    pub fn app(&self) -> &AppId {
        &self.app
    }

    pub fn transport(&self) -> Arc<dyn Transport> {
        self.transport.clone()
    }

    pub fn rate(&self) -> RateState {
        let g = self.gate.lock().unwrap();
        RateState {
            hourly_remaining: g.hourly_remaining,
            daily_remaining: g.daily_remaining,
            blocked_for: g.blocked_until.map(|t| t.saturating_duration_since(Instant::now()).as_secs()).unwrap_or(0),
            calls: g.calls,
        }
    }

    fn app_headers(&self) -> Vec<(String, String)> {
        vec![
            ("Application-Name".into(), self.app.name.clone()),
            ("Application-Version".into(), self.app.version.clone()),
            ("User-Agent".into(), self.app.user_agent()),
        ]
    }

    /// Waits for the gap, refuses while blocked or out of budget.
    fn admit(&self) -> Result<()> {
        let mut g = self.gate.lock().unwrap();
        let now = Instant::now();
        if g.hourly_reset.is_some_and(|t| now >= t) {
            g.hourly_remaining = None;
            g.hourly_reset = None;
        }
        if g.daily_reset.is_some_and(|t| now >= t) {
            g.daily_remaining = None;
            g.daily_reset = None;
        }
        if let Some(t) = g.blocked_until {
            let left = t.saturating_duration_since(Instant::now());
            if !left.is_zero() {
                return Err(Error::new(ErrorCode::Busy, format!("Nexus Mods asked Kiln to wait {} s.", left.as_secs().max(1))));
            }
            g.blocked_until = None;
        }
        if g.hourly_remaining.is_some_and(|n| n < RESERVE) || g.daily_remaining.is_some_and(|n| n < RESERVE) {
            return Err(Error::new(ErrorCode::Busy, "Nexus Mods' request limit for your account is nearly used up. Try again later."));
        }
        if let Some(last) = g.last {
            let wait = self.gap.saturating_sub(last.elapsed());
            if !wait.is_zero() {
                std::thread::sleep(wait);
            }
        }
        g.last = Some(Instant::now());
        g.calls += 1;
        Ok(())
    }

    fn note(&self, resp: &HttpResponse) {
        let mut g = self.gate.lock().unwrap();
        let num = |h: &str| resp.header(h).and_then(|v| v.trim().parse::<i64>().ok());
        if let Some(n) = num("x-rl-hourly-remaining") {
            g.hourly_remaining = Some(n);
            g.hourly_reset = Some(deadline(reset_seconds(resp.header("x-rl-hourly-reset"), 3600)));
        }
        if let Some(n) = num("x-rl-daily-remaining") {
            g.daily_remaining = Some(n);
            g.daily_reset = Some(deadline(reset_seconds(resp.header("x-rl-daily-reset"), 86400)));
        }
        if resp.status == 429 {
            g.blocked_until = Some(deadline(retry_seconds(resp.header("retry-after"))));
        }
    }

    fn request(&self, method: &'static str, path: &str, body: Option<serde_json::Value>, need_auth: bool) -> Result<HttpResponse> {
        super::require_enabled()?;
        let _request = self.request_lock.lock().unwrap();
        let mut token = self.credentials.access_token()?;
        if need_auth && token.is_none() {
            return Err(Error::new(ErrorCode::AuthFailed, "Sign in to Nexus Mods in Settings first."));
        }
        self.admit()?;
        let mut headers = self.app_headers();
        headers.push(("Accept".into(), "application/json".into()));
        if let Some(token) = &token {
            headers.push(("Authorization".into(), format!("Bearer {token}")));
        }
        let body = body.map(|b| {
            headers.push(("Content-Type".into(), "application/json".into()));
            serde_json::to_vec(&b).unwrap_or_default()
        });
        let mut req = HttpRequest { method, url: format!("{}{}", self.origin, path), headers, body };
        let mut resp = self.transport.send(&req)?;
        self.note(&resp);
        if resp.status == 401 {
            if let Some(rejected) = token.as_deref() {
                if let Some(fresh) = self.credentials.refresh_rejected(rejected)? {
                    self.admit()?;
                    req.headers.retain(|(name,_)| !name.eq_ignore_ascii_case("authorization"));
                    req.headers.push(("Authorization".into(), format!("Bearer {fresh}")));
                    token = Some(fresh);
                    resp = self.transport.send(&req)?;
                    self.note(&resp);
                }
            }
            if resp.status == 401 { if let Some(rejected) = token.as_deref() { self.credentials.forget_rejected(rejected)?; } }
        }
        match resp.status {
            200..=299 => Ok(resp),
            401 => Err(Error::new(ErrorCode::AuthFailed, "Nexus Mods authorization expired or was revoked. Sign in again.")),
            403 => Err(Error::new(ErrorCode::Refused, "Nexus Mods refused this request. Check your account and download permissions.")),
            404 => Err(Error::not_found("Not found on Nexus Mods.")),
            410 => Err(Error::new(ErrorCode::Refused, "The download link has expired. Click Mod Manager Download on the site again.")),
            429 => Err(Error::new(ErrorCode::Busy, format!("Nexus Mods asked Kiln to slow down. Try again in {} s.", self.rate().blocked_for.max(1)))),
            451 => Err(Error::new(ErrorCode::Refused, "Nexus Mods withholds this mod in your country without age verification.")),
            s => Err(Error::new(ErrorCode::Network, format!("Nexus Mods answered {s}."))),
        }
    }

    fn data<T: serde::de::DeserializeOwned>(resp: &HttpResponse, what: &str) -> Result<T> {
        #[derive(Deserialize)]
        struct Wrap<T> {
            data: T,
        }
        serde_json::from_slice::<Wrap<T>>(&resp.body).map(|w| w.data).map_err(|_| Error::new(ErrorCode::Parse, format!("Nexus Mods {what}: invalid response")))
    }

    /// One page of `POST /mods/search` for `game` (cursor paging, `page_size` ≤ 100).
    pub fn search(&self, game: &str, sort: &str, adult: &str, cursor: Option<&str>, page_size: u32) -> Result<SearchPage> {
        check_game(game)?;
        let mut pagination = serde_json::json!({"type": "cursor", "page_size": page_size.clamp(1, 100)});
        if let Some(c) = cursor {
            pagination["cursor"] = c.into();
        }
        let body = serde_json::json!({
            "filters": {"games": [game], "adult": adult},
            "sort": {"field": sort, "direction": "desc"},
            "pagination": pagination,
        });
        let resp = self.request("POST", "/v3/mods/search", Some(body), false)?;
        #[derive(Deserialize)]
        struct Data {
            mods: Vec<ModSummary>,
        }
        #[derive(Deserialize, Default)]
        struct Meta {
            #[serde(default)]
            next_cursor: Option<String>,
            #[serde(default)]
            total_count: Option<u64>,
        }
        #[derive(Deserialize)]
        struct Whole {
            data: Data,
            #[serde(default)]
            meta: Option<Meta>,
        }
        let w: Whole = serde_json::from_slice(&resp.body).map_err(|_| Error::new(ErrorCode::Parse, "Nexus Mods search: invalid response"))?;
        let meta = w.meta.unwrap_or_default();
        Ok(SearchPage { mods: w.data.mods, next_cursor: meta.next_cursor.filter(|c| !c.is_empty()), total: meta.total_count })
    }

    /// `GET /games/{game}/mods/{id}` (no key needed).
    pub fn mod_details(&self, game: &str, gsid: &str) -> Result<ModDetails> {
        check_game(game)?;
        check_digits(gsid)?;
        Self::data(&self.request("GET", &format!("/v3/games/{game}/mods/{gsid}"), None, false)?, "mod")
    }

    /// `GET /mods/{uid}/files` (key needed).
    pub fn mod_files(&self, mod_uid: &str) -> Result<Vec<ModFile>> {
        check_digits(mod_uid)?;
        #[derive(Deserialize)]
        struct D {
            mod_files: Vec<ModFile>,
        }
        Ok(Self::data::<D>(&self.request("GET", &format!("/v3/mods/{mod_uid}/files"), None, true)?, "files")?.mod_files)
    }

    /// `GET /mod-files/{id}/versions` (key needed).
    pub fn file_versions(&self, file_id: &str) -> Result<Vec<FileVersion>> {
        check_digits(file_id)?;
        #[derive(Deserialize)]
        struct D {
            versions: Vec<FileVersion>,
        }
        Ok(Self::data::<D>(&self.request("GET", &format!("/v3/mod-files/{file_id}/versions"), None, true)?, "file versions")?.versions)
    }

    /// `GET /games/{game}/mod-file-versions/{gsid}`: the version an nxm link names.
    pub fn file_version_by_gsid(&self, game: &str, gsid: &str) -> Result<FileVersion> {
        check_game(game)?;
        check_digits(gsid)?;
        Self::data(&self.request("GET", &format!("/v3/games/{game}/mod-file-versions/{gsid}"), None, true)?, "file version")
    }

    /// `GET /mod-file-versions/{id}/dependencies`: the raw ranges (DLCs are left out).
    pub fn dependencies(&self, version_id: &str) -> Result<Vec<Dependency>> {
        check_digits(version_id)?;
        let resp = self.request("GET", &format!("/v3/mod-file-versions/{version_id}/dependencies"), None, true)?;
        // documented without the data wrapper; accept both
        let v: serde_json::Value = serde_json::from_slice(&resp.body).map_err(|_| Error::new(ErrorCode::Parse, "Nexus Mods dependencies: invalid response"))?;
        let defs = v.get("data").unwrap_or(&v).get("dependency_definitions").cloned().unwrap_or(serde_json::Value::Array(vec![]));
        serde_json::from_value(defs).map_err(|_| Error::new(ErrorCode::Parse, "Nexus Mods dependencies: invalid response"))
    }

    /// `GET /games/{game}/mod-file-versions/{gsid}/download-link`: signed CDN links (4 h). `key`/`expires` from an nxm
    /// link; premium users need neither.
    pub fn download_links(&self, game: &str, gsid: &str, key: Option<&str>, expires: Option<i64>) -> Result<Vec<DownloadLink>> {
        check_game(game)?;
        check_digits(gsid)?;
        if key.is_some() != expires.is_some() || key.is_some_and(|k| k.is_empty() || k.len() > 256 || k.chars().any(char::is_control)) || expires.is_some_and(|e| e <= 0) {
            return Err(Error::invalid("Invalid Mod Manager Download credentials."));
        }
        let mut path = format!("/v3/games/{game}/mod-file-versions/{gsid}/download-link");
        if let (Some(k), Some(e)) = (key, expires) {
            path.push_str(&format!("?key={}&expires={e}", urlencode(k)));
        }
        #[derive(Deserialize)]
        struct D {
            links: Vec<DownloadLink>,
        }
        let links = Self::data::<D>(&self.request("GET", &path, None, true)?, "download links")?.links;
        if links.is_empty() {
            return Err(Error::new(ErrorCode::Network, "Nexus Mods gave no download link."));
        }
        Ok(links)
    }

    /// v1 `GET /v1/users/validate.json`: the authorized account (name, premium). Nothing else of the answer is kept.
    pub fn validate(&self) -> Result<Account> {
        let resp = self.request("GET", "/v1/users/validate.json", None, true)?;
        let v: serde_json::Value = serde_json::from_slice(&resp.body).map_err(|_| Error::new(ErrorCode::Parse, "Nexus Mods account: invalid response"))?;
        let flag = |k: &str| v.get(k).and_then(|x| x.as_bool()).unwrap_or(false);
        Ok(Account { name: v.get("name").and_then(|n| n.as_str()).unwrap_or("").to_string(), premium: flag("is_premium") || flag("is_premium?") })
    }

    /// An archive's manifest (`manifest_url`): a public file on Nexus's storage, fetched without account credentials.
    pub fn manifest(&self, url: &str) -> Result<Vec<ManifestEntry>> {
        super::require_enabled()?;
        if !url.starts_with("https://") {
            return Err(Error::new(ErrorCode::Refused, "the archive manifest is not on https"));
        }
        let mut headers = self.app_headers();
        headers.push(("Accept".into(), "application/json".into()));
        let resp = self.transport.send(&HttpRequest { method: "GET", url: url.into(), headers, body: None })?;
        if resp.status != 200 {
            return Err(Error::new(ErrorCode::Network, format!("the archive manifest answered {}", resp.status)));
        }
        let v: serde_json::Value = serde_json::from_slice(&resp.body).map_err(|_| Error::new(ErrorCode::Parse, "archive manifest: invalid response"))?;
        Ok(parse_manifest(&v))
    }

    /// Opens a download link (the CDN; no key).
    pub fn open_download(&self, url: &str) -> Result<HttpStream> {
        super::require_enabled()?;
        if !url.starts_with("https://") {
            return Err(Error::new(ErrorCode::Refused, "Nexus Mods gave a download link that is not https"));
        }
        let s = self.transport.open(url, &self.app_headers())?;
        if !(200..300).contains(&s.status) {
            return Err(Error::new(ErrorCode::Network, format!("the download answered {}", s.status)));
        }
        Ok(s)
    }
}

fn check_digits(id: &str) -> Result<()> {
    if id.is_empty() || !id.bytes().all(|b| b.is_ascii_digit()) || id.len() > 24 || id.bytes().all(|b| b == b'0') {
        return Err(Error::invalid("Invalid Nexus Mods id."));
    }
    Ok(())
}

fn check_game(game: &str) -> Result<()> {
    if game == super::catalog::GAME { Ok(()) } else { Err(Error::invalid("Only Enshrouded is supported.")) }
}

fn reset_seconds(value: Option<&str>, fallback: u64) -> u64 {
    value.and_then(|s| s.trim().parse::<i64>().ok().or_else(|| crate::util::parse_rfc3339(s.trim())))
        .map(|t| t.saturating_sub(crate::util::now_unix()).max(1) as u64).unwrap_or(fallback)
}

fn retry_seconds(value: Option<&str>) -> u64 {
    let Some(value) = value else { return 60 };
    if let Ok(n) = value.trim().parse::<u64>() { return n.max(1); }
    // Retry-After also permits IMF-fixdate (HTTP date), not just a delta in seconds.
    let parts: Vec<_> = value.split_ascii_whitespace().collect();
    if let [_, day, month, year, time, "GMT"] = parts.as_slice()
        && let Some(month) = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"].iter().position(|m| m == month)
        && let Some(t) = crate::util::parse_rfc3339(&format!("{year}-{:02}-{day}T{time}Z", month + 1))
    { return t.saturating_sub(crate::util::now_unix()).max(1) as u64; }
    60
}

fn deadline(seconds: u64) -> Instant {
    let now = Instant::now();
    // Unrepresentable server values must still block instead of disabling the gate or panicking.
    now.checked_add(Duration::from_secs(seconds)).unwrap_or_else(|| now + Duration::from_secs(365 * 86400))
}

pub fn urlencode(s: &str) -> String {
    let mut out = String::new();
    for b in s.bytes() {
        if b.is_ascii_alphanumeric() || b"-_.~".contains(&b) {
            out.push(b as char);
        } else {
            out.push_str(&format!("%{b:02X}"));
        }
    }
    out
}

/// Reads an archive manifest whatever its exact layout: a tree (`children`, `name`/`path`, `type`) as the legacy file
/// preview had, or a flat list (`files`/`entries` of `{path, size, sha256|hash}`). Directories are left out.
pub fn parse_manifest(v: &serde_json::Value) -> Vec<ManifestEntry> {
    let mut out = Vec::new();
    walk(v, "", &mut out, 0);
    out.sort_by(|a, b| a.path.cmp(&b.path));
    out.dedup_by(|a, b| a.path == b.path);
    out
}

fn walk(v: &serde_json::Value, prefix: &str, out: &mut Vec<ManifestEntry>, depth: usize) {
    if depth > 64 {
        return;
    }
    match v {
        serde_json::Value::Array(items) => items.iter().for_each(|i| walk(i, prefix, out, depth + 1)),
        serde_json::Value::Object(o) => {
            let s = |k: &str| o.get(k).and_then(|x| x.as_str());
            let name = s("path").or_else(|| s("file_path")).or_else(|| s("filename")).or_else(|| s("name"));
            let kids = ["children", "files", "entries", "contents"].iter().find_map(|k| o.get(*k).filter(|x| x.is_array() || x.is_object()));
            let is_dir = s("type").is_some_and(|t| t.eq_ignore_ascii_case("directory") || t.eq_ignore_ascii_case("dir")) || (kids.is_some() && o.get("size").is_none());
            if let Some(n) = name {
                let n = n.replace('\\', "/");
                let full = if o.contains_key("path") || n.contains('/') || prefix.is_empty() { n.trim_start_matches('/').to_string() } else { format!("{prefix}/{n}") };
                if !is_dir && !full.is_empty() && !full.ends_with('/') {
                    let size = o.get("size").and_then(|x| x.as_u64().or_else(|| x.as_str().and_then(parse_size)));
                    let sha256 = ["sha256", "hash", "checksum"].iter().find_map(|k| s(k)).map(|h| h.to_ascii_lowercase())
                        .filter(|h| h.len() == 64 && h.bytes().all(|b| b.is_ascii_hexdigit()));
                    out.push(ManifestEntry { path: full.clone(), size, sha256 });
                }
                if let Some(k) = kids {
                    walk(k, if is_dir { &full } else { prefix }, out, depth + 1);
                }
            } else if let Some(k) = kids {
                walk(k, prefix, out, depth + 1);
            } else {
                o.values().filter(|x| x.is_array() || x.is_object()).for_each(|x| walk(x, prefix, out, depth + 1));
            }
        }
        _ => {}
    }
}

/// "1234" or "1.2 kB" (the legacy preview wrote sizes as text): bytes. Text sizes are rounded, so they are not used to
/// check files (only exact integers are).
fn parse_size(s: &str) -> Option<u64> {
    s.trim().parse::<u64>().ok()
}

#[cfg(test)]
mod safety_tests {
    use super::*;
    use std::sync::atomic::{AtomicUsize, Ordering};

    struct Reply {
        response: HttpResponse,
        calls: AtomicUsize,
        active: AtomicUsize,
        peak: AtomicUsize,
    }

    impl Transport for Reply {
        fn send(&self, _: &HttpRequest) -> Result<HttpResponse> {
            self.calls.fetch_add(1, Ordering::SeqCst);
            let active = self.active.fetch_add(1, Ordering::SeqCst) + 1;
            self.peak.fetch_max(active, Ordering::SeqCst);
            std::thread::sleep(Duration::from_millis(5));
            self.active.fetch_sub(1, Ordering::SeqCst);
            Ok(self.response.clone())
        }
        fn open(&self, _: &str, _: &[(String, String)]) -> Result<HttpStream> { unreachable!() }
    }

    fn client(response: HttpResponse) -> (Arc<Reply>, Client) {
        let reply = Arc::new(Reply { response, calls: AtomicUsize::new(0), active: AtomicUsize::new(0), peak: AtomicUsize::new(0) });
        let client = Client::new(reply.clone(), Some("test-secret".into()), AppId::kiln_launcher("test")).without_gap();
        (reply, client)
    }

    #[test]
    fn errors_and_debug_output_do_not_echo_credentials() {
        let response = HttpResponse { status: 403, body: br#"{"detail":"test-secret"}"#.to_vec(), ..Default::default() };
        let (_, c) = client(response.clone());
        assert!(!format!("{:?}", c.validate().unwrap_err()).contains("test-secret"));
        assert!(!format!("{response:?}").contains("test-secret"));
        let request = HttpRequest { method: "GET", url: format!("{API_ORIGIN}/v3/test?key=test-secret"), headers: vec![("apikey".into(), "test-secret".into())], body: Some(b"test-secret".to_vec()) };
        assert!(!format!("{request:?}").contains("test-secret"));
        let response = HttpResponse { status: 200, body: br#"{"data":{"id":"test-secret"}}"#.to_vec(), ..Default::default() };
        assert!(!format!("{:?}", Client::data::<u32>(&response, "test").unwrap_err()).contains("test-secret"));
        #[cfg(feature = "http")]
        assert!(!net("https://test-secret@cdn.test/?key=test-secret", "test-secret").message.contains("test-secret"));
    }

    #[test]
    fn either_budget_stops_requests_until_its_reset() {
        for header in ["x-rl-hourly-remaining", "x-rl-daily-remaining"] {
            let (reply, c) = client(HttpResponse { status: 200, headers: vec![(header.into(), "3".into())], body: br#"{"name":"test"}"#.to_vec() });
            c.validate().unwrap();
            assert_eq!(c.validate().unwrap_err().code, ErrorCode::Busy);
            assert_eq!(reply.calls.load(Ordering::SeqCst), 1);
            {
                let mut g = c.gate.lock().unwrap();
                g.hourly_reset = Some(Instant::now());
                g.daily_reset = Some(Instant::now());
            }
            c.validate().unwrap();
            assert_eq!(reply.calls.load(Ordering::SeqCst), 2);
        }
    }

    #[test]
    fn concurrent_callers_serialize_through_the_response_budget() {
        let (reply, c) = client(HttpResponse { status: 200, headers: vec![], body: br#"{"name":"test"}"#.to_vec() });
        std::thread::scope(|scope| {
            for _ in 0..8 { scope.spawn(|| c.validate().unwrap()); }
        });
        assert_eq!(reply.calls.load(Ordering::SeqCst), 8);
        assert_eq!(reply.peak.load(Ordering::SeqCst), 1);
        let (reply, c) = client(HttpResponse { status: 429, headers: vec![("Retry-After".into(), "7200".into())], body: vec![] });
        std::thread::scope(|scope| {
            for _ in 0..8 { scope.spawn(|| assert_eq!(c.validate().unwrap_err().code, ErrorCode::Busy)); }
        });
        assert_eq!(reply.calls.load(Ordering::SeqCst), 1);
        assert!(c.rate().blocked_for > 3600);
    }

    #[test]
    fn rate_dates_and_unrepresentable_delays_are_bounded_without_disabling_the_gate() {
        assert_eq!(retry_seconds(Some("Thu, 01 Jan 2099 00:00:00 GMT")), crate::util::parse_rfc3339("2099-01-01T00:00:00Z").unwrap().saturating_sub(crate::util::now_unix()) as u64);
        assert!(reset_seconds(Some("2099-01-01T00:00:00Z"), 1) > 3600);
        assert!(deadline(u64::MAX) > Instant::now());
        assert_eq!(retry_seconds(Some("invalid")), 60);
    }

    #[test]
    fn invalid_game_ids_and_authorization_never_reach_transport() {
        let (reply, c) = client(HttpResponse::default());
        assert!(c.mod_details("enshrouded/../../users", "1").is_err());
        assert!(c.mod_details("enshrouded", "0").is_err());
        assert!(c.download_links("enshrouded", "1", Some("x"), None).is_err());
        assert!(c.download_links("enshrouded", "1", Some("x\n"), Some(1)).is_err());
        assert_eq!(reply.calls.load(Ordering::SeqCst), 0);
    }

    #[cfg(feature = "http")]
    #[test]
    fn real_transport_refuses_api_key_to_foreign_origins_before_connecting() {
        let transport = UreqTransport::new("Kiln-test");
        for url in ["http://api.nexusmods.com/test", "https://api.nexusmods.com.evil.test/test", "https://api.nexusmods.com@evil.test/test", "https://cdn.test/test"] {
            let req = HttpRequest { method: "GET", url: url.into(), headers: vec![("APIKEY".into(), "test-secret".into())], body: None };
            assert_eq!(transport.send(&req).unwrap_err().code, ErrorCode::Refused);
        }
        assert!(api_url("https://api.nexusmods.com/v3/mods/search"));
        assert!(transport.open("https://cdn.test/file", &[("apikey".into(), "test-secret".into())]).is_err());
        let bearer = vec![("Authorization".into(), "Bearer test-secret".into())];
        for url in ["https://cdn.test/file", "https://api.nexusmods.com.evil.test/file", "https://users.nexusmods.com/oauth/userinfo"] {
            assert!(transport.send(&HttpRequest { method: "GET", url: url.into(), headers: bearer.clone(), body: None }).is_err());
        }
        assert!(transport.open("https://cdn.test/file", &bearer).is_err());
    }
}
