//! The GitHub provider of update checks: anonymous reads of a public repository's releases.
//!
//! What it asks, and nothing else:
//! - `GET https://api.github.com/repos/<owner>/<repo>/releases?per_page=100&page=<n>`: at most five pages, one after
//!   the other. The repository comes from the mod's source; no other mod, no installed version, no hash and nothing
//!   about the player or the PC is in any request.
//! - `GET https://api.github.com/repos/<owner>/<repo>/releases/assets/<id>` for the candidate release's
//!   `kiln-release.json` (at most 256 KiB), with at most one redirect to GitHub's two asset hosts. No other asset is
//!   ever requested: a check never downloads a mod.
//!
//! No credential is sent, and none can be: every host of this provider's policy is a plain host, so the HTTP layer
//! refuses a credential header before anything leaves ([`crate::net::Client::send`]). The User-Agent names Kiln and
//! its version only.
//!
//! What it remembers is in [`super::cache`]: a source answered recently is not asked again (30 minutes for a check
//! the player started, a day for the automatic one; `refresh` asks anyway), the list is asked for conditionally
//! (ETag, Last-Modified: a 304 confirms the earlier answer), a request limit (403 or 429 with GitHub's limit
//! headers) stops every further request to GitHub until its reset and is not lifted by `refresh`, and any other
//! failure waits 5 minutes, doubling up to a day. A failure never replaces an earlier success: that one is shown,
//! marked stale, and never as "up to date".
//!
//! Written from GitHub's public REST documentation ("Releases", "Release assets", "Rate limits for the REST API",
//! API version 2022-11-28). No request of it has been sent to GitHub by its authors; it is proven against a fake
//! transport and hand-written answers only.

use super::cache::{self, Attempt, CacheEntry, CacheFile, InstalledIdentity};
use super::coordinator::{CheckItem, CheckOptions, UpdateProvider};
use super::github_select::{self as select, Fetched, GithubSource, Installed, Outcome, Release};
use super::status::{CandidateIdentity, ModUpdateStatus, Provider, UnknownReason};
use super::{POLICY_VERSION, STALE_AFTER_SECS};
use crate::manifest::UpdatesSpec;
use crate::net::{Client, Policy, Request, Response, Transport};
use crate::paths::DataDirs;
use crate::task::Progress;
use crate::{Error, ErrorCode, Result};
use std::collections::{BTreeMap, HashMap};
use std::sync::{Arc, Condvar, Mutex};

/// The API host (the same name `kiln publish` talks to: `publish::github::API_HOST`).
pub const API_HOST: &str = "api.github.com";
/// The two hosts GitHub redirects an asset read to.
pub const ASSET_HOSTS: [&str; 2] = ["release-assets.githubusercontent.com", "objects.githubusercontent.com"];
const API_VERSION: &str = "2022-11-28";
pub const PER_PAGE: usize = 100;
pub const MAX_PAGES: usize = 5;
/// The bound of one page of the release list.
pub const MAX_PAGE_BYTES: u64 = 1024 * 1024;
/// The time bound of a request of an update check: for connecting and for the answer (the real transport is made
/// with it: `UreqTransport::with_timeouts`).
pub const REQUEST_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(10);
/// A check the player started does not ask again about a source answered within this time (as the Nexus catalog).
pub const MANUAL_TTL_SECS: i64 = 1800;

/// The hosts an update check may reach at GitHub, none of them with a credential.
pub fn policy() -> Policy {
    Policy::new("GitHub").plain_host(API_HOST).plain_host(ASSET_HOSTS[0]).plain_host(ASSET_HOSTS[1])
}

/// `kiln/<version>`: the tool and its version, nothing about the player or the PC.
pub fn user_agent() -> String {
    format!("kiln/{}", crate::VERSION)
}

/// What asking for a repository's release list gave.
#[derive(Debug, Clone)]
enum Listing {
    Read { releases: Vec<Release>, complete: bool, etag: Option<String>, last_modified: Option<String>, digest: String },
    /// 304 to a conditional request made with this ETag / Last-Modified.
    NotModified { etag: Option<String>, last_modified: Option<String> },
    Failed { reason: UnknownReason, until: Option<i64> },
}

/// A list being read. `done`: None while it is read; then the answer, or `Some(None)` when the reader gave up (its
/// check was canceled) and whoever waits has to ask for itself.
#[derive(Default)]
struct Slot {
    done: Mutex<Option<Option<Listing>>>,
    ready: Condvar,
}

pub struct GithubProvider<'a> {
    dirs: &'a DataDirs,
    client: Client<'a>,
    /// Lists being read right now, by repository: a second check that wants the same list waits for the first one's
    /// answer instead of asking again.
    in_flight: Mutex<HashMap<String, Arc<Slot>>>,
}

/// A header value that may be sent back as it came: short, visible ASCII.
fn validator(r: &Response, name: &str) -> Option<String> {
    r.header(name).filter(|v| !v.is_empty() && v.len() <= 256 && v.bytes().all(|b| (0x20..=0x7e).contains(&b))).map(str::to_string)
}

/// A request limit in an answer: when it ends. GitHub's documented signals: 429, or 403 with `retry-after`, or with
/// `x-ratelimit-remaining: 0` and `x-ratelimit-reset` (Unix seconds).
fn limit_of(r: &Response, now: i64) -> Option<i64> {
    let retry_after = r.header("retry-after").and_then(|s| s.trim().parse::<i64>().ok()).filter(|s| *s >= 0);
    let exhausted = r.header("x-ratelimit-remaining").map(str::trim) == Some("0");
    let reset = r.header("x-ratelimit-reset").and_then(|s| s.trim().parse::<i64>().ok());
    if !(r.status == 429 || (r.status == 403 && (retry_after.is_some() || exhausted))) {
        return None;
    }
    // a minute at least; an hour when GitHub names no time
    Some(retry_after.map(|s| now.saturating_add(s)).or(reset.filter(|_| exhausted)).unwrap_or(now + 3600).max(now + 60))
}

/// The last answer used up the budget: stop before the next request is refused.
fn budget_spent(r: &Response, now: i64) -> Option<i64> {
    if r.header("x-ratelimit-remaining").map(str::trim) != Some("0") {
        return None;
    }
    Some(r.header("x-ratelimit-reset").and_then(|s| s.trim().parse::<i64>().ok()).unwrap_or(now + 3600).max(now + 60))
}

/// One check's working state.
struct Run<'r> {
    opts: &'r CheckOptions,
    cache: CacheFile,
    changed: BTreeMap<String, CacheEntry>,
    limits_changed: bool,
    /// Lists read in this check, by repository (lower case): a repository of several mods is listed once.
    lists: HashMap<String, Listing>,
    /// The cache keys asked about in this check.
    asked: Vec<String>,
}

impl<'a> GithubProvider<'a> {
    /// `transport`: the real one in the launcher and the CLI, the fake in tests. `user_agent`: [`user_agent`].
    pub fn new(dirs: &'a DataDirs, transport: &'a dyn Transport, user_agent: &str) -> GithubProvider<'a> {
        GithubProvider { dirs, client: Client::new(policy(), transport, user_agent), in_flight: Mutex::default() }
    }

    fn get(&self, url: String) -> Request {
        Request::get(url).header("Accept", "application/vnd.github+json").header("X-GitHub-Api-Version", API_VERSION)
    }

    /// Reads a repository's release list: up to [`MAX_PAGES`] pages, serially. `conditional`: the validators of the
    /// answer that is remembered (sent with the first page only).
    fn read_list(&self, repository: &str, conditional: Option<(Option<String>, Option<String>)>, run: &mut Run, progress: &mut dyn Progress) -> Result<Listing> {
        let now = run.opts.now;
        let mut releases = Vec::new();
        let mut digest = sha2_of();
        let (mut etag, mut last_modified) = (None, None);
        for page in 1..=MAX_PAGES {
            if progress.canceled() {
                return Err(Error::new(ErrorCode::Canceled, "canceled"));
            }
            if run.cache.limit_left(API_HOST, now) > 0 {
                return Ok(Listing::Failed { reason: UnknownReason::RateLimited, until: run.cache.limits.get(API_HOST).map(|l| l.until) });
            }
            let mut req = self.get(format!("https://{API_HOST}/repos/{repository}/releases?per_page={PER_PAGE}&page={page}")).max_response(MAX_PAGE_BYTES);
            if page == 1
                && let Some((e, m)) = &conditional
            {
                if let Some(e) = e {
                    req = req.header("If-None-Match", e);
                }
                if let Some(m) = m {
                    req = req.header("If-Modified-Since", m);
                }
            }
            let r = match self.client.send(req) {
                Ok(r) => r,
                // no answer, an answer over its bound, a redirect: nothing usable either way
                Err(_) => return Ok(Listing::Failed { reason: UnknownReason::Network, until: None }),
            };
            if let Some(until) = limit_of(&r, now) {
                run.cache.limit(API_HOST, now, until);
                run.limits_changed = true;
                return Ok(Listing::Failed { reason: UnknownReason::RateLimited, until: Some(until) });
            }
            if let Some(until) = budget_spent(&r, now) {
                run.cache.limit(API_HOST, now, until);
                run.limits_changed = true;
            }
            match r.status {
                200 => {}
                304 if page == 1 && conditional.is_some() => {
                    let (e, m) = conditional.unwrap_or_default();
                    return Ok(Listing::NotModified { etag: e, last_modified: m });
                }
                404 | 410 | 451 => return Ok(Listing::Failed { reason: UnknownReason::NotFound, until: None }),
                _ => return Ok(Listing::Failed { reason: UnknownReason::Network, until: None }),
            }
            let Some(items) = select::parse_page(&r.body) else {
                return Ok(Listing::Failed { reason: UnknownReason::Network, until: None });
            };
            // A page's validator says nothing about later pages. Only a short first page covers the whole list.
            if page == 1 && items.len() < PER_PAGE {
                etag = validator(&r, "etag");
                last_modified = validator(&r, "last-modified");
            }
            sha2_update(&mut digest, &r.body);
            let n = items.len();
            releases.extend(items);
            if n < PER_PAGE {
                return Ok(Listing::Read { releases, complete: true, etag, last_modified, digest: sha2_hex(digest) });
            }
            // a full page that GitHub says is the last one
            let more = r.header("link").is_none_or(|l| l.contains("rel=\"next\""));
            if !more {
                return Ok(Listing::Read { releases, complete: true, etag, last_modified, digest: sha2_hex(digest) });
            }
        }
        // five full pages and more to come: what was read does not show the newest for sure
        Ok(Listing::Read { releases, complete: false, etag, last_modified, digest: sha2_hex(digest) })
    }

    /// The list of a repository for this check: from this check's own earlier read, from another check that is
    /// reading it right now, or by asking.
    fn listing(&self, repository: &str, entry: &CacheEntry, run: &mut Run, progress: &mut dyn Progress) -> Result<Listing> {
        let repo_key = repository.to_ascii_lowercase();
        // only an entry with a remembered selection can make use of "nothing changed"
        let conditional = entry.selection.as_ref().map(|_| (entry.etag.clone(), entry.last_modified.clone())).filter(|(e, m)| e.is_some() || m.is_some());
        let usable = |l: &Listing| match l {
            // "not modified" speaks for the answer whose validators were sent
            Listing::NotModified { etag, last_modified } => conditional.as_ref().is_some_and(|(e, m)| e == etag && m == last_modified),
            _ => true,
        };
        if let Some(l) = run.lists.get(&repo_key).filter(|l| usable(l)) {
            return Ok(l.clone());
        }
        let (slot, mine) = {
            let mut map = self.in_flight.lock().unwrap();
            match map.get(&repo_key) {
                Some(s) => (s.clone(), false),
                None => {
                    let s = Arc::new(Slot::default());
                    map.insert(repo_key.clone(), s.clone());
                    (s, true)
                }
            }
        };
        if !mine {
            let mut done = slot.done.lock().unwrap();
            while done.is_none() {
                done = slot.ready.wait(done).unwrap();
            }
            if let Some(l) = done.clone().flatten().filter(|l| usable(l)) {
                run.lists.insert(repo_key, l.clone());
                return Ok(l);
            }
            drop(done);
            // the other check gave up, or its answer does not serve this entry (other validators): ask after all
            let l = self.read_list(repository, conditional, run, progress)?;
            run.lists.insert(repo_key, l.clone());
            return Ok(l);
        }
        let got = self.read_list(repository, conditional, run, progress);
        *slot.done.lock().unwrap() = Some(got.as_ref().ok().cloned());
        slot.ready.notify_all();
        self.in_flight.lock().unwrap().remove(&repo_key);
        let l = got?;
        run.lists.insert(repo_key, l.clone());
        Ok(l)
    }

    /// Reads the candidate's `kiln-release.json` through the asset endpoint.
    fn read_binding(&self, repository: &str, asset_id: &str, run: &mut Run) -> Fetched {
        if run.cache.limit_left(API_HOST, run.opts.now) > 0 {
            return Fetched::Unreadable;
        }
        // GitHub's asset ids are numbers; anything else does not go into a URL
        if asset_id.is_empty() || !asset_id.bytes().all(|b| b.is_ascii_digit()) {
            return Fetched::Refused;
        }
        let now = run.opts.now;
        let req = Request::get(format!("https://{API_HOST}/repos/{repository}/releases/assets/{asset_id}"))
            .header("Accept", "application/octet-stream")
            .header("X-GitHub-Api-Version", API_VERSION)
            .max_response(crate::release_binding::MAX_BYTES as u64)
            .allow_redirect_to(&ASSET_HOSTS);
        match self.client.send(req) {
            Ok(r) => {
                if let Some(until) = limit_of(&r, now).or_else(|| budget_spent(&r, now)) {
                    run.cache.limit(API_HOST, now, until);
                    run.limits_changed = true;
                }
                if r.status == 200 { Fetched::Bytes(r.body) } else { Fetched::Unreadable }
            }
            // refused by the HTTP layer: a redirect to another host or a second one, an answer over 256 KiB, an
            // address that is not public
            Err(e) if e.code == ErrorCode::Refused => Fetched::Refused,
            Err(_) => Fetched::Unreadable,
        }
    }

    /// Asks GitHub about one source and writes the outcome into its entry.
    fn refresh(&self, item: &CheckItem, src: &GithubSource, entry: &mut CacheEntry, run: &mut Run, progress: &mut dyn Progress) -> Result<()> {
        let now = run.opts.now;
        let mut conditional_entry = entry.clone();
        if entry.receipt.release_id.as_deref() != item.receipt.as_ref().and_then(|r| r.release_id.as_deref()) {
            // The selection only checked whether the receipt known then was deleted. A 304 cannot answer for a
            // different receipt: read the list again and check that release's presence too.
            conditional_entry.etag = None;
            conditional_entry.last_modified = None;
        }
        match self.listing(src.repository, &conditional_entry, run, progress)? {
            Listing::Failed { reason, until } => entry.failed(now, reason, until),
            Listing::NotModified { .. } => entry.succeeded(now, Attempt::NotModified),
            Listing::Read { releases, complete, etag, last_modified, digest } => {
                let receipt_release = item.receipt.as_ref().and_then(|r| r.release_id.as_deref());
                let mut unreadable = false;
                let selection = select::select(src, &item.target.mod_id, &releases, complete, receipt_release, |_, asset| {
                    let got = self.read_binding(src.repository, &asset.id, run);
                    unreadable = got == Fetched::Unreadable;
                    got
                });
                if unreadable {
                    // the binding of the release that would win could not be read: no new answer, the old one stays
                    let limited = run.cache.limits.contains_key(API_HOST);
                    let until = run.cache.limits.get(API_HOST).map(|l| l.until);
                    entry.failed(now, if limited { UnknownReason::RateLimited } else { UnknownReason::Network }, until.filter(|_| limited));
                } else {
                    entry.etag = etag;
                    entry.last_modified = last_modified;
                    entry.response_digest = Some(digest);
                    entry.selection_coverage = Some(selection.coverage);
                    entry.selection = Some(selection);
                    entry.succeeded(now, Attempt::Ok);
                }
            }
        }
        Ok(())
    }

    fn check_one(&self, item: &CheckItem, run: &mut Run, progress: &mut dyn Progress) -> Result<ModUpdateStatus> {
        let UpdatesSpec::Github { repository, tag_pattern, asset, channel } = &item.spec else {
            return Ok(item.status(false));
        };
        let src = GithubSource { repository, tag_pattern, asset: asset.as_deref(), channel: *channel };
        let opts = run.opts;
        let now = opts.now;
        let key = cache::github_key(repository, tag_pattern, asset.as_deref(), &item.target.mod_id, *channel);
        let mut entry = run.changed.get(&key).or_else(|| run.cache.entries.get(&key)).cloned().unwrap_or_else(|| CacheEntry {
            provider: Provider::Github,
            destination: repository.to_ascii_lowercase(),
            tag_pattern: tag_pattern.clone(),
            asset: asset.clone(),
            mod_id: item.target.mod_id.clone(),
            channel: *channel,
            policy_version: POLICY_VERSION,
            last_success: None,
            last_attempt: None,
            etag: None,
            last_modified: None,
            response_digest: None,
            selection_coverage: None,
            next_allowed_at: None,
            status: None,
            reason: None,
            failures: 0,
            receipt: InstalledIdentity::default(),
            selection: None,
        });
        let before = entry.clone();
        let receipt_release = item.receipt.as_ref().and_then(|r| r.release_id.as_deref());
        let receipt_changed = entry.selection.is_some() && entry.receipt.release_id.as_deref() != receipt_release;

        // Whether to ask, and if not, why not (None: because the remembered answer is recent enough).
        let ttl = if opts.automatic { STALE_AFTER_SECS } else { MANUAL_TTL_SECS };
        let held: Option<Option<UnknownReason>> = if opts.offline {
            Some(Some(UnknownReason::Network))
        } else if !run.asked.contains(&key) && run.asked.len() >= cache::MAX_SOURCES {
            Some(Some(UnknownReason::NotChecked))
        } else {
            let limit_before = run.cache.limits.get(API_HOST).copied();
            let limited = run.cache.limit_left(API_HOST, now) > 0;
            run.limits_changed |= run.cache.limits.get(API_HOST).copied() != limit_before;
            // looked at even under a limit: after a clock rollback both waits start again from the same moment
            let entry_wait = entry.wait_left(now);
            if limited {
                // GitHub's request limit: nothing is asked until it ends, whatever the player asks
                Some(Some(UnknownReason::RateLimited))
            } else if entry_wait > 0 && (entry.reason == Some(UnknownReason::RateLimited) || !opts.refresh) {
                // the wait after a failure; --refresh skips it unless the failure was a request limit
                Some(entry.reason)
            } else if !opts.refresh && !receipt_changed && entry.status != Some(Attempt::Failed) && entry.success_within(now, ttl) {
                Some(None)
            } else {
                None
            }
        };
        if held.is_none() {
            run.asked.push(key.clone());
            self.refresh(item, &src, &mut entry, run, progress)?;
        }
        let receipt = item.receipt.as_ref();
        let receipt_confirmed = !receipt_changed || (held.is_none() && entry.status != Some(Attempt::Failed));
        entry.receipt = InstalledIdentity {
            version: item.target.library_ref.version.clone(),
            hash: item.target.library_ref.hash.clone(),
            artifact_sha256: receipt.and_then(|r| r.artifact_sha256.clone()),
            release_id: if receipt_confirmed { receipt_release.map(str::to_string) } else { before.receipt.release_id.clone() },
        };
        if entry != before && !opts.offline {
            run.changed.insert(key, entry.clone());
        }

        // The status: the remembered selection compared with what is installed now.
        let mut s = item.status(false);
        s.page_url = Some(select::release_page(repository, None));
        if !receipt_confirmed {
            return Ok(s.unknown(entry.reason.or(held.flatten().filter(|_| !opts.offline)).unwrap_or(UnknownReason::NotChecked)));
        }
        let failed = entry.status == Some(Attempt::Failed);
        let Some(selection) = &entry.selection else {
            // never answered: the failure's reason, the request limit, or simply "not checked yet"
            let why = entry.reason.filter(|_| failed).or(held.flatten().filter(|r| *r == UnknownReason::RateLimited));
            return Ok(s.unknown(why.unwrap_or(UnknownReason::NotChecked)));
        };
        let why_not_now = entry.reason.filter(|_| failed).or(held.flatten());
        let installed = Installed {
            version: &item.target.library_ref.version,
            artifact_sha256: receipt.and_then(|r| r.artifact_sha256.as_deref()),
            release_id: receipt.and_then(|r| r.release_id.as_deref()),
            asset_id: receipt.and_then(|r| r.asset_id.as_deref()),
            side: item.target.side,
        };
        let verdict = select::compare(&installed, selection, &opts.api_version);
        s.state = verdict.state;
        s.reason = verdict.reason;
        s.compatibility = verdict.compatibility;
        s.coverage = selection.coverage;
        s.checked_at = entry.last_success.map(crate::util::rfc3339);
        if let Outcome::Candidate(c) = &selection.outcome {
            s.candidate_version = Some(c.version.clone());
            s.candidate_identity = Some(CandidateIdentity {
                release_id: Some(c.release_id.clone()),
                tag: Some(c.tag.clone()),
                asset_id: c.asset_id.clone(),
                asset_name: c.asset_name.clone(),
                artifact_sha256: c.artifact_sha256.clone(),
                file_version_id: None,
            });
            s.page_url = Some(select::release_page(repository, Some(&c.tag)));
        }
        // stale: the answer is older than a day (or cannot be dated), or a later try to refresh it failed
        let fresh = entry.success_within(now, STALE_AFTER_SECS) && !failed;
        Ok(if fresh { s } else { s.into_stale(why_not_now.unwrap_or(UnknownReason::Network)) })
    }
}

fn sha2_of() -> sha2::Sha256 {
    use sha2::Digest;
    sha2::Sha256::new()
}

fn sha2_update(h: &mut sha2::Sha256, bytes: &[u8]) {
    use sha2::Digest;
    h.update((bytes.len() as u64).to_le_bytes());
    h.update(bytes);
}

fn sha2_hex(h: sha2::Sha256) -> String {
    use sha2::Digest;
    crate::hash::hex(h.finalize().as_slice())
}

impl UpdateProvider for GithubProvider<'_> {
    fn provider(&self) -> Provider {
        Provider::Github
    }

    fn check(&self, items: &[CheckItem], opts: &CheckOptions, progress: &mut dyn Progress) -> Result<Vec<ModUpdateStatus>> {
        let mut run = Run { opts, cache: cache::load(self.dirs), changed: BTreeMap::new(), limits_changed: false, lists: HashMap::new(), asked: Vec::new() };
        let mut out = Vec::with_capacity(items.len());
        let mut stopped = None;
        for (i, item) in items.iter().enumerate() {
            if progress.canceled() {
                stopped = Some(Error::new(ErrorCode::Canceled, "canceled"));
                break;
            }
            progress.report(Some(i as f32 / items.len().max(1) as f32), &format!("Checking {}", item.target.name));
            match self.check_one(item, &mut run, progress) {
                Ok(s) => out.push(s),
                Err(e) => {
                    stopped = Some(e);
                    break;
                }
            }
        }
        // what was learned before a cancel is kept
        if !opts.offline {
            cache::save(self.dirs, &run.changed, &run.cache.limits, run.limits_changed)?;
        }
        match stopped {
            Some(e) => Err(e),
            None => Ok(out),
        }
    }
}

#[cfg(all(test, feature = "zip"))]
mod tests {
    /// One host name for GitHub's API in the publisher and in the update check.
    #[test]
    fn the_api_host_is_the_publishers() {
        assert_eq!(super::API_HOST, crate::publish::github::API_HOST);
    }
}
