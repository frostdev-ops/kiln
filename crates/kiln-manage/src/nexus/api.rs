//! The Nexus Mods API client: v3 (`https://api.nexusmods.com/v3`, OpenAPI at api.nexusmods.com/openapi.yaml) for mods,
//! files, versions, dependencies and download links; v1 `users/validate.json` only for the account's name and premium
//! state (v3 has no user endpoint).
//!
//! Nexus's rules for apps, as this client follows them:
//! - Every request names the app: `Application-Name` + `Application-Version` and a `User-Agent` of the same; nothing
//!   personal (no user name, no machine name).
//! - The user's API key goes only to the API host (`apikey` header), never to the CDN or the archive manifests, and is
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

/// The API host. The key is sent to this origin only.
pub const API_ORIGIN: &str = "https://api.nexusmods.com";
/// The smallest gap between two API requests of one client.
pub const MIN_GAP: Duration = Duration::from_millis(150);
/// Requests stop when fewer than this many remain in the hour (or the day), so other apps of the user keep some.
pub const RESERVE: i64 = 20;

// ---------------------------------------------------------------- transport

#[derive(Debug, Clone)]
pub struct HttpRequest {
    pub method: &'static str,
    pub url: String,
    pub headers: Vec<(String, String)>,
    pub body: Option<Vec<u8>>,
}

#[derive(Debug, Clone, Default)]
pub struct HttpResponse {
    pub status: u16,
    pub headers: Vec<(String, String)>,
    pub body: Vec<u8>,
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
    /// GET with a streamed body (no API key is ever passed here: the URLs are the CDN's).
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
            .max_redirects(5)
            .user_agent(user_agent)
            .build()
            .into();
        UreqTransport { agent }
    }
}

#[cfg(feature = "http")]
fn net(url: &str, e: impl std::fmt::Display) -> Error {
    // the URL never carries the API key (a header); download links carry Nexus's signature, so only the host is named
    let host = url.split("://").nth(1).and_then(|r| r.split('/').next()).unwrap_or("Nexus Mods");
    Error::new(ErrorCode::Network, format!("{host}: {e}"))
}

#[cfg(feature = "http")]
impl Transport for UreqTransport {
    fn send(&self, req: &HttpRequest) -> Result<HttpResponse> {
        let mut resp = match req.method {
            "POST" => {
                let mut b = self.agent.post(&req.url);
                for (k, v) in &req.headers {
                    b = b.header(k.as_str(), v.as_str());
                }
                b.send(req.body.as_deref().unwrap_or_default()).map_err(|e| net(&req.url, e))?
            }
            _ => {
                let mut b = self.agent.get(&req.url);
                for (k, v) in &req.headers {
                    b = b.header(k.as_str(), v.as_str());
                }
                b.call().map_err(|e| net(&req.url, e))?
            }
        };
        let status = resp.status().as_u16();
        let headers = resp.headers().iter().map(|(k, v)| (k.as_str().to_string(), v.to_str().unwrap_or("").to_string())).collect();
        let body = resp.body_mut().with_config().limit(32 * 1024 * 1024).read_to_vec().map_err(|e| net(&req.url, e))?;
        Ok(HttpResponse { status, headers, body })
    }

    fn open(&self, url: &str, headers: &[(String, String)]) -> Result<HttpStream> {
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

pub struct Client {
    origin: String,
    transport: Arc<dyn Transport>,
    key: Option<String>,
    app: AppId,
    gate: Mutex<Gate>,
    gap: Duration,
}

impl std::fmt::Debug for Client {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Client").field("origin", &self.origin).field("key", &self.key.as_ref().map(|_| "<hidden>")).field("app", &self.app).finish()
    }
}

fn problem(body: &[u8]) -> Option<String> {
    let v: serde_json::Value = serde_json::from_slice(body).ok()?;
    let d = v.get("detail").and_then(|d| d.as_str()).or_else(|| v.get("message").and_then(|d| d.as_str())).or_else(|| v.get("title").and_then(|d| d.as_str()))?;
    Some(d.chars().take(300).collect())
}

impl Client {
    pub fn new(transport: Arc<dyn Transport>, key: Option<String>, app: AppId) -> Client {
        Client::with_origin(API_ORIGIN, transport, key, app)
    }

    /// Tests point the client at a mock origin.
    pub fn with_origin(origin: &str, transport: Arc<dyn Transport>, key: Option<String>, app: AppId) -> Client {
        let key = key.map(|k| k.trim().to_string()).filter(|k| !k.is_empty());
        Client { origin: origin.trim_end_matches('/').to_string(), transport, key, app, gate: Mutex::new(Gate::default()), gap: MIN_GAP }
    }

    /// Tests: no gap between requests.
    pub fn without_gap(mut self) -> Client {
        self.gap = Duration::ZERO;
        self
    }

    pub fn has_key(&self) -> bool {
        self.key.is_some()
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
        if let Some(t) = g.blocked_until {
            let left = t.saturating_duration_since(Instant::now());
            if !left.is_zero() {
                return Err(Error::new(ErrorCode::Busy, format!("Nexus Mods asked Kiln to wait {} s.", left.as_secs().max(1))));
            }
            g.blocked_until = None;
        }
        if g.hourly_remaining.is_some_and(|n| n < RESERVE) && g.daily_remaining.is_some_and(|n| n < RESERVE) {
            return Err(Error::new(ErrorCode::Busy, "Nexus Mods' request limit for your key is nearly used up. Try again later."));
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
        }
        if let Some(n) = num("x-rl-daily-remaining") {
            g.daily_remaining = Some(n);
        }
        if resp.status == 429 {
            let secs = num("retry-after").unwrap_or(60).clamp(1, 3600) as u64;
            g.blocked_until = Some(Instant::now() + Duration::from_secs(secs));
        }
    }

    fn request(&self, method: &'static str, path: &str, body: Option<serde_json::Value>, need_key: bool) -> Result<HttpResponse> {
        if need_key && self.key.is_none() {
            return Err(Error::new(ErrorCode::AuthFailed, "Add your Nexus Mods API key in Settings first."));
        }
        self.admit()?;
        let mut headers = self.app_headers();
        headers.push(("Accept".into(), "application/json".into()));
        if let Some(k) = &self.key {
            headers.push(("apikey".into(), k.clone()));
        }
        let body = body.map(|b| {
            headers.push(("Content-Type".into(), "application/json".into()));
            serde_json::to_vec(&b).unwrap_or_default()
        });
        let req = HttpRequest { method, url: format!("{}{}", self.origin, path), headers, body };
        let resp = self.transport.send(&req)?;
        self.note(&resp);
        let detail = || problem(&resp.body);
        match resp.status {
            200..=299 => Ok(resp),
            401 => Err(Error::new(ErrorCode::AuthFailed, "Nexus Mods did not accept the API key. Check it in Settings.")),
            403 => Err(Error::new(ErrorCode::Refused, detail().unwrap_or_else(|| "Nexus Mods refused this.".into()))),
            404 => Err(Error::not_found(detail().unwrap_or_else(|| "Not found on Nexus Mods.".into()))),
            410 => Err(Error::new(ErrorCode::Refused, "The download link has expired. Click Mod Manager Download on the site again.")),
            429 => Err(Error::new(ErrorCode::Busy, format!("Nexus Mods asked Kiln to slow down. Try again in {} s.", self.rate().blocked_for.max(1)))),
            451 => Err(Error::new(ErrorCode::Refused, "Nexus Mods withholds this mod in your country without age verification.")),
            s => Err(Error::new(ErrorCode::Network, format!("Nexus Mods answered {s}{}", detail().map(|d| format!(": {d}")).unwrap_or_default()))),
        }
    }

    fn data<T: serde::de::DeserializeOwned>(resp: &HttpResponse, what: &str) -> Result<T> {
        #[derive(Deserialize)]
        struct Wrap<T> {
            data: T,
        }
        serde_json::from_slice::<Wrap<T>>(&resp.body).map(|w| w.data).map_err(|e| Error::new(ErrorCode::Parse, format!("Nexus Mods {what}: {e}")))
    }

    /// One page of `POST /mods/search` for `game` (cursor paging, `page_size` ≤ 100).
    pub fn search(&self, game: &str, sort: &str, adult: &str, cursor: Option<&str>, page_size: u32) -> Result<SearchPage> {
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
        let w: Whole = serde_json::from_slice(&resp.body).map_err(|e| Error::new(ErrorCode::Parse, format!("Nexus Mods search: {e}")))?;
        let meta = w.meta.unwrap_or_default();
        Ok(SearchPage { mods: w.data.mods, next_cursor: meta.next_cursor.filter(|c| !c.is_empty()), total: meta.total_count })
    }

    /// `GET /games/{game}/mods/{id}` (no key needed).
    pub fn mod_details(&self, game: &str, gsid: &str) -> Result<ModDetails> {
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
        check_digits(gsid)?;
        Self::data(&self.request("GET", &format!("/v3/games/{game}/mod-file-versions/{gsid}"), None, true)?, "file version")
    }

    /// `GET /mod-file-versions/{id}/dependencies`: the raw ranges (DLCs are left out).
    pub fn dependencies(&self, version_id: &str) -> Result<Vec<Dependency>> {
        check_digits(version_id)?;
        let resp = self.request("GET", &format!("/v3/mod-file-versions/{version_id}/dependencies"), None, true)?;
        // documented without the data wrapper; accept both
        let v: serde_json::Value = serde_json::from_slice(&resp.body).map_err(|e| Error::new(ErrorCode::Parse, format!("Nexus Mods dependencies: {e}")))?;
        let defs = v.get("data").unwrap_or(&v).get("dependency_definitions").cloned().unwrap_or(serde_json::Value::Array(vec![]));
        serde_json::from_value(defs).map_err(|e| Error::new(ErrorCode::Parse, format!("Nexus Mods dependencies: {e}")))
    }

    /// `GET /games/{game}/mod-file-versions/{gsid}/download-link`: signed CDN links (4 h). `key`/`expires` from an nxm
    /// link; premium users need neither.
    pub fn download_links(&self, game: &str, gsid: &str, key: Option<&str>, expires: Option<i64>) -> Result<Vec<DownloadLink>> {
        check_digits(gsid)?;
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

    /// v1 `GET /v1/users/validate.json`: the key's account (name, premium). Nothing else of the answer is kept.
    pub fn validate(&self) -> Result<Account> {
        let resp = self.request("GET", "/v1/users/validate.json", None, true)?;
        let v: serde_json::Value = serde_json::from_slice(&resp.body).map_err(|e| Error::new(ErrorCode::Parse, format!("Nexus Mods account: {e}")))?;
        let flag = |k: &str| v.get(k).and_then(|x| x.as_bool()).unwrap_or(false);
        Ok(Account { name: v.get("name").and_then(|n| n.as_str()).unwrap_or("").to_string(), premium: flag("is_premium") || flag("is_premium?") })
    }

    /// An archive's manifest (`manifest_url`): a public file on Nexus's storage, fetched without the key.
    pub fn manifest(&self, url: &str) -> Result<Vec<ManifestEntry>> {
        if !url.starts_with("https://") {
            return Err(Error::new(ErrorCode::Refused, "the archive manifest is not on https"));
        }
        let mut headers = self.app_headers();
        headers.push(("Accept".into(), "application/json".into()));
        let resp = self.transport.send(&HttpRequest { method: "GET", url: url.into(), headers, body: None })?;
        if resp.status != 200 {
            return Err(Error::new(ErrorCode::Network, format!("the archive manifest answered {}", resp.status)));
        }
        let v: serde_json::Value = serde_json::from_slice(&resp.body).map_err(|e| Error::new(ErrorCode::Parse, format!("archive manifest: {e}")))?;
        Ok(parse_manifest(&v))
    }

    /// Opens a download link (the CDN; no key).
    pub fn open_download(&self, url: &str) -> Result<HttpStream> {
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
    if id.is_empty() || !id.bytes().all(|b| b.is_ascii_digit()) || id.len() > 24 {
        return Err(Error::invalid(format!("'{id}' is not a Nexus Mods id")));
    }
    Ok(())
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
