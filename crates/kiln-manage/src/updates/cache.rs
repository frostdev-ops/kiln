//! What update checks remember between runs: `<data>/updates/cache.json`. One entry per source of one mod.
//!
//! The key holds everything that changes what an answer means: provider, destination, tag pattern, asset template,
//! mod id, channel and the policy version. An entry keeps the last successful selection (independent of what is
//! installed: the comparison is made again each time), when it was made, the validators of the provider's answer
//! (ETag, Last-Modified), the digest of that answer, whether the list was read to its end, the last attempt and its
//! outcome, when the next request is allowed, and the identity of what was installed at the time.
//!
//! The file is a cache: one that cannot be read (damaged, another format, larger than 10 MiB) is dropped and built
//! again. Losing it costs requests, never correctness: without an entry a mod reads `unknown / not_checked`.
//!
//! Clock rules. A time from the future (the PC's clock went backwards) never makes an answer fresh, and a wait that
//! was running starts again from now with its original length: a rollback neither unlocks a request limit nor
//! blocks checks until the old date comes round again.

use super::github_select::Selection;
use super::status::{Coverage, Provider, UnknownReason};
use super::{POLICY_VERSION, STALE_AFTER_SECS};
use crate::manifest::UpdateChannel;
use crate::paths::DataDirs;
use crate::Result;
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::path::PathBuf;

pub const FILE_NAME: &str = "cache.json";
pub const FORMAT: u32 = 1;
/// The file never grows beyond this: the entries asked longest ago go first.
pub const MAX_BYTES: usize = 10 * 1024 * 1024;
/// The most sources one check asks a provider about.
pub const MAX_SOURCES: usize = 500;
/// No wait is longer than this, whatever a provider's header says.
pub const MAX_WAIT_SECS: i64 = STALE_AFTER_SECS;
/// The first wait after a failed request; it doubles with each further failure, up to [`MAX_WAIT_SECS`].
pub const FIRST_BACKOFF_SECS: i64 = 300;

/// How the last attempt ended.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum Attempt {
    /// The provider answered and the answer was read.
    Ok,
    /// The provider said nothing changed (304): the earlier success stands, confirmed now.
    NotModified,
    /// No usable answer; `reason` says why.
    Failed,
}

/// What was installed when the entry was last written.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InstalledIdentity {
    pub version: String,
    pub hash: String,
    pub artifact_sha256: Option<String>,
    pub release_id: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CacheEntry {
    pub provider: Provider,
    pub destination: String,
    pub tag_pattern: String,
    pub asset: Option<String>,
    pub mod_id: String,
    pub channel: UpdateChannel,
    pub policy_version: u32,
    /// Unix seconds of the last answer that was read (or confirmed by a 304).
    pub last_success: Option<i64>,
    pub last_attempt: Option<i64>,
    pub etag: Option<String>,
    pub last_modified: Option<String>,
    /// SHA-256 over the release list as it was answered.
    pub response_digest: Option<String>,
    pub selection_coverage: Option<Coverage>,
    /// No request for this source before this time.
    pub next_allowed_at: Option<i64>,
    pub status: Option<Attempt>,
    /// Why the last attempt failed.
    pub reason: Option<UnknownReason>,
    /// Failed attempts in a row.
    #[serde(default)]
    pub failures: u32,
    #[serde(default)]
    pub receipt: InstalledIdentity,
    /// The last successful selection.
    pub selection: Option<Selection>,
}

impl CacheEntry {
    /// Whether the last success is recent enough to stand without a mark: within `within` seconds before `now`. A
    /// success dated after `now` is not fresh.
    pub fn success_within(&self, now: i64, within: i64) -> bool {
        self.last_success.is_some_and(|t| t <= now && now - t <= within)
    }

    /// Seconds until a request is allowed again (0: now). A last attempt dated after `now` means the clock went
    /// backwards: the wait is then taken at its original length, counted from now, and written back that way, so it
    /// ends.
    pub fn wait_left(&mut self, now: i64) -> i64 {
        let Some(next) = self.next_allowed_at else { return 0 };
        if let Some(at) = self.last_attempt.filter(|at| *at > now) {
            let wait = (next - at).clamp(0, MAX_WAIT_SECS);
            self.last_attempt = Some(now);
            self.next_allowed_at = Some(now + wait);
            return wait;
        }
        (next - now).clamp(0, MAX_WAIT_SECS)
    }

    pub fn succeeded(&mut self, now: i64, attempt: Attempt) {
        self.last_success = Some(now);
        self.last_attempt = Some(now);
        self.status = Some(attempt);
        self.reason = None;
        self.failures = 0;
        self.next_allowed_at = None;
    }

    /// A failed attempt: the earlier success, if any, is kept as it is. `until`: when the provider said to come back
    /// (a request limit); None: the wait doubles with each failure in a row.
    pub fn failed(&mut self, now: i64, reason: UnknownReason, until: Option<i64>) {
        self.last_attempt = Some(now);
        self.status = Some(Attempt::Failed);
        self.reason = Some(reason);
        self.failures = self.failures.saturating_add(1);
        let wait = match until {
            Some(t) => (t - now).clamp(0, MAX_WAIT_SECS),
            None => backoff(self.failures),
        };
        self.next_allowed_at = Some(now + wait);
    }
}

/// The wait after `failures` failed attempts in a row: 5 minutes, doubling, at most a day.
pub fn backoff(failures: u32) -> i64 {
    let doubled = FIRST_BACKOFF_SECS.checked_shl(failures.saturating_sub(1).min(20)).unwrap_or(MAX_WAIT_SECS);
    doubled.clamp(FIRST_BACKOFF_SECS, MAX_WAIT_SECS)
}

/// A provider-wide stop: its request limit is used up until `until`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Limit {
    /// When it was learned.
    pub since: i64,
    pub until: i64,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CacheFile {
    pub format: u32,
    #[serde(default)]
    pub entries: BTreeMap<String, CacheEntry>,
    /// Request limits in force, by provider host.
    #[serde(default)]
    pub limits: BTreeMap<String, Limit>,
}

impl CacheFile {
    /// Seconds until `host` may be asked again (0: now), with the same clock rule as an entry's wait.
    pub fn limit_left(&mut self, host: &str, now: i64) -> i64 {
        let Some(l) = self.limits.get_mut(host) else { return 0 };
        if l.since > now {
            let wait = (l.until - l.since).clamp(0, MAX_WAIT_SECS);
            *l = Limit { since: now, until: now + wait };
            return wait;
        }
        let left = (l.until - now).clamp(0, MAX_WAIT_SECS);
        if left == 0 {
            self.limits.remove(host);
        }
        left
    }

    pub fn limit(&mut self, host: &str, now: i64, until: i64) {
        let until = now + (until - now).clamp(0, MAX_WAIT_SECS);
        self.limits.insert(host.to_string(), Limit { since: now, until });
    }

    /// The file's JSON within [`MAX_BYTES`]: entries asked longest ago are dropped until it fits.
    fn bounded_json(&mut self) -> Result<Vec<u8>> {
        loop {
            let bytes = serde_json::to_vec_pretty(self)?;
            if bytes.len() <= MAX_BYTES || self.entries.is_empty() {
                return Ok(bytes);
            }
            // drop a tenth at a time, oldest attempt first (never asked sorts first)
            let mut by_age: Vec<(i64, String)> = self.entries.iter().map(|(k, e)| (e.last_attempt.unwrap_or(i64::MIN), k.clone())).collect();
            by_age.sort();
            for (_, k) in by_age.into_iter().take((self.entries.len() / 10).max(1)) {
                self.entries.remove(&k);
            }
        }
    }
}

/// The key of a GitHub source of one mod.
pub fn github_key(repository: &str, tag_pattern: &str, asset: Option<&str>, mod_id: &str, channel: UpdateChannel) -> String {
    format!("github|{}|{tag_pattern}|{}|{mod_id}|{}|p{POLICY_VERSION}", repository.to_ascii_lowercase(), asset.unwrap_or(""), channel.as_str())
}

/// `<data>/updates/cache.json`
pub fn path_of(dirs: &DataDirs) -> PathBuf {
    super::root_of(dirs).join(FILE_NAME)
}

/// Reads the cache. Anything but a readable file of this format and size is an empty cache.
pub fn load(dirs: &DataDirs) -> CacheFile {
    let path = path_of(dirs);
    let empty = || CacheFile { format: FORMAT, ..Default::default() };
    if std::fs::metadata(&path).is_ok_and(|m| m.len() > MAX_BYTES as u64) {
        return empty();
    }
    match crate::util::read_json::<CacheFile>(&path) {
        Ok(Some(mut f)) if f.format == FORMAT => {
            // answers made under other rules are not answers any more
            f.entries.retain(|_, e| e.policy_version == POLICY_VERSION);
            f
        }
        _ => empty(),
    }
}

/// Writes the entries and limits a check changed. The file is read again under the lock first, so two checks that
/// ran at the same time (the launcher and the CLI) do not lose each other's entries.
pub fn save(dirs: &DataDirs, changed: &BTreeMap<String, CacheEntry>, limits: &BTreeMap<String, Limit>, limits_changed: bool) -> Result<()> {
    if changed.is_empty() && !limits_changed {
        return Ok(());
    }
    let _lock = super::lock(dirs)?;
    let mut file = load(dirs);
    file.format = FORMAT;
    for (k, e) in changed {
        file.entries.insert(k.clone(), e.clone());
    }
    if limits_changed {
        file.limits = limits.clone();
    }
    let bytes = file.bounded_json()?;
    crate::util::write_atomic(&path_of(dirs), &bytes)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn entry() -> CacheEntry {
        CacheEntry {
            provider: Provider::Github, destination: "example-org/lantern-glow".into(), tag_pattern: "v{version}".into(), asset: None,
            mod_id: "lantern-glow".into(), channel: UpdateChannel::Stable, policy_version: POLICY_VERSION, last_success: None, last_attempt: None,
            etag: None, last_modified: None, response_digest: None, selection_coverage: None, next_allowed_at: None, status: None, reason: None,
            failures: 0, receipt: InstalledIdentity::default(), selection: None,
        }
    }

    const NOW: i64 = 1_800_000_000;

    #[test]
    fn the_key_holds_everything_that_changes_what_an_answer_means() {
        let base = github_key("Example-Org/Lantern-Glow", "v{version}", Some("glow-{version}.zip"), "lantern-glow", UpdateChannel::Stable);
        assert_eq!(base, format!("github|example-org/lantern-glow|v{{version}}|glow-{{version}}.zip|lantern-glow|stable|p{POLICY_VERSION}"));
        assert_eq!(base, github_key("example-org/lantern-glow", "v{version}", Some("glow-{version}.zip"), "lantern-glow", UpdateChannel::Stable), "one repository, however it is spelled");
        let others = [
            github_key("example-org/other", "v{version}", Some("glow-{version}.zip"), "lantern-glow", UpdateChannel::Stable),
            github_key("example-org/lantern-glow", "glow-{version}", Some("glow-{version}.zip"), "lantern-glow", UpdateChannel::Stable),
            github_key("example-org/lantern-glow", "v{version}", None, "lantern-glow", UpdateChannel::Stable),
            github_key("example-org/lantern-glow", "v{version}", Some("glow-{version}.zip"), "night-sky", UpdateChannel::Stable),
            github_key("example-org/lantern-glow", "v{version}", Some("glow-{version}.zip"), "lantern-glow", UpdateChannel::Prerelease),
        ];
        for (i, k) in others.iter().enumerate() {
            assert_ne!(*k, base, "{i}");
            assert!(others.iter().filter(|o| *o == k).count() == 1, "{i}");
        }
    }

    #[test]
    fn failures_back_off_to_a_day_and_a_success_clears_them() {
        assert_eq!([1, 2, 3, 4, 9, 10, 40, u32::MAX].map(backoff), [300, 600, 1200, 2400, 76800, 86400, 86400, 86400]);
        assert_eq!(backoff(0), 300);
        let mut e = entry();
        e.failed(NOW, UnknownReason::Network, None);
        assert_eq!((e.next_allowed_at, e.failures, e.status, e.reason), (Some(NOW + 300), 1, Some(Attempt::Failed), Some(UnknownReason::Network)));
        e.failed(NOW + 300, UnknownReason::Network, None);
        assert_eq!(e.next_allowed_at, Some(NOW + 300 + 600));
        // a provider's own date wins over the doubling, but no wait is longer than a day
        e.failed(NOW, UnknownReason::RateLimited, Some(NOW + 1800));
        assert_eq!(e.next_allowed_at, Some(NOW + 1800));
        e.failed(NOW, UnknownReason::RateLimited, Some(NOW + 400 * 86400));
        assert_eq!(e.next_allowed_at, Some(NOW + 86400));
        e.failed(NOW, UnknownReason::RateLimited, Some(NOW - 5));
        assert_eq!(e.next_allowed_at, Some(NOW), "a date already past is no wait");
        e.succeeded(NOW + 10, Attempt::Ok);
        assert_eq!((e.failures, e.reason, e.next_allowed_at, e.last_success), (0, None, None, Some(NOW + 10)));
    }

    #[test]
    fn a_clock_that_went_backwards_neither_frees_nor_blocks() {
        let mut e = entry();
        e.succeeded(NOW, Attempt::Ok);
        assert!(e.success_within(NOW + 60, 1800) && !e.success_within(NOW + 1801, 1800));
        assert!(!e.success_within(NOW - 60, 86400), "a success from the future is not fresh");
        // limited for another 40 minutes when the clock jumps back two days
        e.failed(NOW, UnknownReason::RateLimited, Some(NOW + 2400));
        let back = NOW - 2 * 86400;
        assert_eq!(e.wait_left(back), 2400, "the wait is not gone");
        assert_eq!((e.last_attempt, e.next_allowed_at), (Some(back), Some(back + 2400)), "and it is counted from now, so it ends");
        assert_eq!(e.wait_left(back + 2399), 1);
        assert_eq!(e.wait_left(back + 2400), 0);
        // the same for the provider-wide limit
        let mut f = CacheFile::default();
        f.limit("api.github.com", NOW, NOW + 3000);
        assert_eq!(f.limit_left("api.github.com", NOW + 1000), 2000);
        assert_eq!(f.limit_left("api.github.com", back), 3000);
        assert_eq!(f.limit_left("api.github.com", back + 2999), 1);
        assert_eq!(f.limit_left("api.github.com", back + 3000), 0);
        assert!(f.limits.is_empty(), "an ended limit is forgotten");
        assert_eq!(f.limit_left("uploads.github.com", NOW), 0);
        // a limit far in the future is cut to a day
        f.limit("api.github.com", NOW, NOW + 90 * 86400);
        assert_eq!(f.limit_left("api.github.com", NOW), 86400);
    }

    fn tmp(tag: &str) -> (DataDirs, PathBuf) {
        let d = std::env::temp_dir().join(format!("kiln-updates-cache-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(&d).unwrap();
        (DataDirs::at(d.clone()), d)
    }

    #[test]
    fn a_cache_that_cannot_be_read_is_an_empty_cache_and_is_built_again() {
        let (dirs, root) = tmp("damaged");
        assert!(load(&dirs).entries.is_empty());
        let mut one = BTreeMap::new();
        one.insert("k1".to_string(), entry());
        save(&dirs, &one, &BTreeMap::new(), false).unwrap();
        assert_eq!(load(&dirs).entries.len(), 1);
        for bad in [&b"{ \"format\": 1, \"entries\": {"[..], b"", b"[]", b"\xff\xfe\x00", br#"{"format": 2, "entries": {}}"#, br#"{"format": 1, "entries": {"k": {"provider": "github"}}}"#] {
            std::fs::write(path_of(&dirs), bad).unwrap();
            assert!(load(&dirs).entries.is_empty(), "{}", String::from_utf8_lossy(bad));
            // and a save over it works
            save(&dirs, &one, &BTreeMap::new(), false).unwrap();
            assert_eq!(load(&dirs).entries.len(), 1);
        }
        // an entry made under other rules is dropped, the others stay
        let mut file = load(&dirs);
        let mut old = entry();
        old.policy_version = POLICY_VERSION + 1;
        file.entries.insert("old".into(), old);
        std::fs::write(path_of(&dirs), serde_json::to_vec(&file).unwrap()).unwrap();
        assert_eq!(load(&dirs).entries.keys().collect::<Vec<_>>(), ["k1"]);
        // two saves do not lose each other's entries
        let mut two = BTreeMap::new();
        two.insert("k2".to_string(), entry());
        save(&dirs, &two, &BTreeMap::new(), false).unwrap();
        assert_eq!(load(&dirs).entries.keys().collect::<Vec<_>>(), ["k1", "k2"]);
        let _ = std::fs::remove_dir_all(root);
    }

    #[test]
    fn the_file_stays_under_its_bound_by_dropping_what_was_asked_longest_ago() {
        let mut f = CacheFile { format: FORMAT, ..Default::default() };
        // each entry carries about 12 KiB, so 1200 of them are well over 10 MiB
        for i in 0..1200 {
            let mut e = entry();
            e.last_attempt = Some(NOW + i);
            e.etag = Some("x".repeat(12 * 1024));
            f.entries.insert(format!("k{i:04}"), e);
        }
        let bytes = f.bounded_json().unwrap();
        assert!(bytes.len() <= MAX_BYTES, "{}", bytes.len());
        assert!(f.entries.len() < 1200 && f.entries.len() > 500, "{}", f.entries.len());
        assert!(f.entries.contains_key("k1199") && !f.entries.contains_key("k0000"), "the newest stay");
        assert!(serde_json::from_slice::<CacheFile>(&bytes).is_ok());
    }
}
