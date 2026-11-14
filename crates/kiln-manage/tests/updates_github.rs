//! The GitHub provider of update checks against a fake transport: what it asks (and what it never does), what it
//! remembers, and that nothing but a read success ends in "up to date". No request leaves the test: every exchange
//! is a `FakeTransport` reply, and `no_request_reached_a_real_host` holds for every test's log by construction (the
//! fake has no socket).

use kiln_manage::library::LibraryRef;
use kiln_manage::manifest::{ModKind, UpdateChannel, UpdatesSpec};
use kiln_manage::net::fake::{FakeTransport, Recorded, Reply};
use kiln_manage::net::{policy, Client, Method, Request, Response, Transport, WireRequest};
use kiln_manage::paths::DataDirs;
use kiln_manage::task::{NoProgress, Progress};
use kiln_manage::updates::coordinator::{CheckItem, CheckOptions, Target, UpdateProvider};
use kiln_manage::updates::github::{self, GithubProvider};
use kiln_manage::updates::receipt::{ReceiptProvider, SourceReceipt};
use kiln_manage::updates::{cache, Compatibility, Coverage, ModUpdateStatus, SourceOrigin, UnknownReason, UpdateState, POLICY_VERSION};
use kiln_manage::ErrorCode;
use serde_json::{json, Value};
use std::path::{Path, PathBuf};

const NOW: i64 = 1_800_000_000;
const HOUR: i64 = 3600;
const DAY: i64 = 86400;
const GLOW: &str = "example-org/lantern-glow";
/// The digest of lantern-glow-1.10.0.zip in the fixture list and its binding.
const GLOW_SHA: &str = "ad9b19c4df20898a8b46b7793aeb81b779db01fc468b2768e193186a1eee003d";
const BINDING_ASSET: &str = "7052";
const ZIP_ASSET: &str = "7051";

struct Tmp(PathBuf);
impl Drop for Tmp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

fn tmp(tag: &str) -> (Tmp, DataDirs) {
    let p = Path::new(env!("CARGO_TARGET_TMPDIR")).join(format!("updates-github-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&p);
    std::fs::create_dir_all(&p).unwrap();
    let dirs = DataDirs::at(p.join("home/some-player/.local/share/kiln"));
    (Tmp(p), dirs)
}

fn fixture(name: &str) -> Vec<u8> {
    std::fs::read(Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixture/updates-github").join(name)).unwrap()
}

fn list_url(repo: &str, page: usize) -> String {
    format!("https://api.github.com/repos/{repo}/releases?per_page=100&page={page}")
}

fn asset_url(repo: &str, id: &str) -> String {
    format!("https://api.github.com/repos/{repo}/releases/assets/{id}")
}

fn github(repo: &str, tag_pattern: &str, asset: Option<&str>, channel: UpdateChannel) -> UpdatesSpec {
    UpdatesSpec::Github { repository: repo.into(), tag_pattern: tag_pattern.into(), asset: asset.map(str::to_string), channel }
}

/// An installed mod with a GitHub source. `archive`: the digest of the archive it was imported from, if Kiln saw one.
fn item(id: &str, version: &str, spec: UpdatesSpec, archive: Option<&str>) -> CheckItem {
    let library_ref = LibraryRef { kind: ModKind::Kiln, id: id.into(), version: version.into(), hash: format!("kdh1:{}", "c0ffee".repeat(10)) };
    let receipt = archive.map(|sha| SourceReceipt {
        library_ref: library_ref.clone(), provider: ReceiptProvider::Local, destination: String::new(), mod_id: id.into(), installed_version: version.into(),
        release_id: None, tag: None, asset_id: None, artifact_sha256: Some(sha.into()), file_chain_id: None, file_version_id: None, metadata_sha256: None,
        policy_version: POLICY_VERSION,
    });
    CheckItem { target: Target { library_ref, name: id.to_uppercase(), mod_id: id.into(), side: None, authored: Some(spec.clone()) }, spec, origin: SourceOrigin::Manifest, receipt }
}

fn glow(version: &str, archive: Option<&str>) -> CheckItem {
    item("lantern-glow", version, github(GLOW, "v{version}", Some("lantern-glow-{version}.zip"), UpdateChannel::Stable), archive)
}

fn opts(now: i64) -> CheckOptions {
    CheckOptions { offline: false, refresh: false, automatic: false, now, api_version: "0.2.3".into() }
}

fn refresh(now: i64) -> CheckOptions {
    CheckOptions { refresh: true, ..opts(now) }
}

fn off(now: i64) -> CheckOptions {
    CheckOptions { offline: true, ..opts(now) }
}

fn check(dirs: &DataDirs, t: &dyn Transport, items: &[CheckItem], o: &CheckOptions) -> Vec<ModUpdateStatus> {
    let got = GithubProvider::new(dirs, t, &github::user_agent()).check(items, o, &mut NoProgress).unwrap();
    assert_eq!(got.len(), items.len());
    for s in &got {
        assert!(s.is_consistent(), "{s:?}");
        assert!(!s.source.as_ref().unwrap().authenticated, "the GitHub check is the anonymous one");
        // the rule above all: nothing stale, and nothing that was not read, is "current"
        assert!(!(s.stale && s.state == UpdateState::Current), "{s:?}");
    }
    got
}

fn one(dirs: &DataDirs, t: &dyn Transport, it: &CheckItem, o: &CheckOptions) -> ModUpdateStatus {
    check(dirs, t, std::slice::from_ref(it), o).remove(0)
}

/// Scripts the answers of one full read of lantern-glow: the list, the binding through its asset endpoint, which
/// redirects once to GitHub's asset host.
fn script_glow(t: &FakeTransport) {
    t.reply(Method::Get, &list_url(GLOW, 1), Reply::bytes(200, &fixture("lantern-glow.releases.json")).with_header("ETag", "W/\"glow-1\"").with_header("Last-Modified", "Sun, 08 Nov 2026 09:00:00 GMT"));
    t.reply(Method::Get, &asset_url(GLOW, BINDING_ASSET), Reply::redirect(302, "https://release-assets.githubusercontent.com/github-production-release-asset/1/2?sig=abc"));
    t.reply(Method::Get, "https://release-assets.githubusercontent.com/github-production-release-asset/1/2?sig=abc", Reply::bytes(200, &fixture("lantern-glow.kiln-release.json")));
}

fn done(t: &FakeTransport) {
    assert!(t.unexpected().is_empty(), "requests nobody scripted: {:?}", t.unexpected());
    assert!(t.unused().is_empty(), "answers nobody asked for: {:?}", t.unused());
}

fn urls(t: &FakeTransport) -> Vec<String> {
    t.requests().into_iter().map(|r| r.url).collect()
}

fn release(id: u64, tag: &str, assets: &[(u64, &str, u64)]) -> Value {
    json!({"id": id, "tag_name": tag, "draft": false, "prerelease": false, "body": "notes",
        "assets": assets.iter().map(|(id, name, size)| json!({"id": id, "name": name, "size": size})).collect::<Vec<_>>()})
}

fn page_of(first: u64, n: u64) -> String {
    Value::Array((0..n).map(|i| release(first + i, &format!("v0.{}.0", first + i), &[])).collect()).to_string()
}

// ------------------------------------------------------------------------------------------- the states

#[test]
fn a_check_reads_the_list_and_the_winners_binding_and_compares() {
    let (_t, dirs) = tmp("states");
    let t = FakeTransport::new();
    script_glow(&t);
    // installed 1.9.0: a higher version is published, and its binding says it loads here
    let s = one(&dirs, &t, &glow("1.9.0", None), &opts(NOW));
    assert_eq!((s.state, s.reason, s.stale, s.coverage, s.compatibility), (UpdateState::Available, None, false, Coverage::Complete, Compatibility::Compatible));
    assert_eq!((s.installed_version.as_str(), s.candidate_version.as_deref()), ("1.9.0", Some("1.10.0")));
    let id = s.candidate_identity.clone().unwrap();
    assert_eq!((id.release_id.as_deref(), id.tag.as_deref(), id.asset_id.as_deref(), id.asset_name.as_deref(), id.artifact_sha256.as_deref()),
        (Some("9005"), Some("v1.10.0"), Some(ZIP_ASSET), Some("lantern-glow-1.10.0.zip"), Some(GLOW_SHA)));
    assert_eq!(s.page_url.as_deref(), Some("https://github.com/example-org/lantern-glow/releases/tag/v1.10.0"));
    assert_eq!(s.checked_at.as_deref(), Some("2027-01-15T08:00:00Z"));
    done(&t);
    assert_eq!(t.requests().len(), 3);
    // the same source for other installed copies: answered from what is remembered, compared anew, no request
    let bound_same = one(&dirs, &t, &glow("1.10.0", Some(GLOW_SHA)), &opts(NOW + 60));
    assert_eq!((bound_same.state, bound_same.reason), (UpdateState::Current, None));
    let bound_other = one(&dirs, &t, &glow("1.10.0", Some(&"b".repeat(64))), &opts(NOW + 60));
    assert_eq!((bound_other.state, bound_other.candidate_version.as_deref()), (UpdateState::NewUpload, Some("1.10.0")));
    let hand_copied = one(&dirs, &t, &glow("1.10.0", None), &opts(NOW + 60));
    assert_eq!((hand_copied.state, hand_copied.reason), (UpdateState::Unknown, Some(UnknownReason::SameVersionUnbound)));
    let ahead = one(&dirs, &t, &glow("1.11.0-dev.1", None), &opts(NOW + 60));
    assert_eq!(ahead.state, UpdateState::Current);
    assert_eq!(t.requests().len(), 3, "{:?}", urls(&t));
}

// ------------------------------------------------------------------------------------------- privacy

fn assert_anonymous(r: &Recorded, secrets: &[String]) {
    const HEADERS: [&str; 5] = ["accept", "x-github-api-version", "user-agent", "if-none-match", "if-modified-since"];
    assert_eq!(r.method, Method::Get, "{}", r.url);
    assert!(r.host == "api.github.com" || github::ASSET_HOSTS.contains(&r.host.as_str()), "host {}", r.host);
    assert!(r.url.starts_with("https://"), "{}", r.url);
    assert_eq!((r.body_len, r.body.len()), (0, 0), "a body in {}", r.url);
    for (name, value) in &r.headers {
        assert!(HEADERS.contains(&name.to_ascii_lowercase().as_str()), "header {name} in {}", r.url);
        assert!(!policy::is_credential_header(name), "credential header {name}");
        for s in secrets {
            assert!(!value.contains(s.as_str()), "header {name} holds {s}");
        }
    }
    assert_eq!(r.header("user-agent"), Some(format!("kiln/{}", kiln_manage::VERSION).as_str()), "a generic User-Agent");
    assert!(r.header("authorization").is_none() && r.header("cookie").is_none());
    for s in secrets {
        assert!(!r.url.contains(s.as_str()), "{} holds {s}", r.url);
    }
}

#[test]
fn no_identity() {
    let (tmp, dirs) = tmp("privacy");
    let t = FakeTransport::new();
    script_glow(&t);
    t.reply(Method::Get, &list_url("example-org/kiln-mods", 1), Reply::bytes(200, &fixture("kiln-mods.releases.json")).with_header("ETag", "\"mods-1\""));
    t.reply(Method::Get, &list_url("example-org/build-tags", 1), Reply::bytes(200, &fixture("build-tags.releases.json")));
    let archive = "5".repeat(64);
    let items = [
        glow("1.9.3-private.7", Some(&archive)),
        item("example", "1.4.0-secret.1", github("example-org/kiln-mods", "mods/example/v{version}", Some("example-{version}.zip"), UpdateChannel::Stable), None),
        item("builds", "1.2.5", github("example-org/build-tags", "v{version}", None, UpdateChannel::Stable), Some(&archive)),
    ];
    let first = check(&dirs, &t, &items, &opts(NOW));
    assert_eq!(first.iter().map(|s| s.state).collect::<Vec<_>>(), [UpdateState::Available, UpdateState::Available, UpdateState::Unknown]);
    // a second, conditional round a day later: the validators go back, nothing else new
    t.reply(Method::Get, &list_url(GLOW, 1), Reply::status(304));
    t.reply(Method::Get, &list_url("example-org/kiln-mods", 1), Reply::status(304));
    t.reply(Method::Get, &list_url("example-org/build-tags", 1), Reply::bytes(200, &fixture("build-tags.releases.json")));
    check(&dirs, &t, &items, &opts(NOW + 2 * DAY));
    done(&t);

    // what must never be in a request: anything about the player, the PC or what is installed
    let user = std::env::var("USER").or_else(|_| std::env::var("USERNAME")).unwrap_or_default();
    let home = std::env::var("HOME").unwrap_or_default();
    let mut secrets: Vec<String> = vec![
        "1.9.3-private.7".into(), "1.4.0-secret.1".into(), "1.2.5".into(), "private".into(), "secret".into(),
        archive.clone(), "c0ffee".into(), "kdh1".into(), "some-player".into(), ".local/share".into(),
        dirs.root.display().to_string(), tmp.0.display().to_string(), "LANTERN-GLOW".into(), "steam".into(), "Steam".into(),
    ];
    for s in [user, home, hostname()] {
        if s.len() >= 3 {
            secrets.push(s);
        }
    }
    let log = t.requests();
    assert_eq!(log.len(), 8, "{:?}", urls(&t));
    let repos = ["example-org/lantern-glow", "example-org/kiln-mods", "example-org/build-tags"];
    let mod_ids = ["lantern-glow", "example", "builds"];
    for r in &log {
        assert!(r.sent, "{}", r.url);
        assert_anonymous(r, &secrets);
        if r.host == "api.github.com" {
            // one repository per request, and so one mod: never a list of what is installed
            let named: Vec<&&str> = repos.iter().filter(|repo| r.url.contains(&format!("/repos/{repo}/"))).collect();
            assert_eq!(named.len(), 1, "{}", r.url);
            let path_and_query = r.url.trim_start_matches("https://api.github.com");
            let rest = path_and_query.replacen(&format!("/repos/{}/", named[0]), "", 1);
            for (repo, id) in repos.iter().zip(mod_ids) {
                assert!(!rest.contains(repo) && !rest.contains(id), "{} also names {repo} / {id}", r.url);
            }
            assert!(rest.starts_with("releases?per_page=100&page=") || rest.starts_with("releases/assets/"), "{}", r.url);
        } else {
            // the redirect's request starts from nothing: the two headers a download needs
            let names: Vec<String> = r.headers.iter().map(|(k, _)| k.to_ascii_lowercase()).collect();
            assert_eq!(names, ["accept", "user-agent"], "{}", r.url);
        }
    }
    // conditional requests carry only what GitHub itself handed out
    let conditional: Vec<&Recorded> = log.iter().filter(|r| r.header("if-none-match").is_some() || r.header("if-modified-since").is_some()).collect();
    assert_eq!(conditional.len(), 2);
    assert_eq!((conditional[0].header("if-none-match"), conditional[0].header("if-modified-since")), (Some("W/\"glow-1\""), Some("Sun, 08 Nov 2026 09:00:00 GMT")));
    // and the layer below refuses a credential for these hosts outright, before anything is sent
    let spy = FakeTransport::new();
    let client = Client::new(github::policy(), &spy, &github::user_agent());
    for header in ["Authorization", "Cookie", "X-GitHub-Token"] {
        for url in [list_url(GLOW, 1), "https://objects.githubusercontent.com/x".to_string()] {
            let e = client.send(Request::get(url).header(header, "Bearer ghp_FAKE0123456789")).unwrap_err();
            assert_eq!(e.code, ErrorCode::Refused, "{header}");
        }
    }
    assert!(spy.requests().is_empty());
    // the files the check wrote hold no credential and no account either
    let cache_text = std::fs::read_to_string(cache::path_of(&dirs)).unwrap();
    assert!(!cache_text.to_ascii_lowercase().contains("token") && !cache_text.contains("some-player"), "{cache_text}");
    println!("PASS updates.no-identity");
}

fn hostname() -> String {
    std::fs::read_to_string("/etc/hostname").map(|s| s.trim().to_string()).unwrap_or_default()
}

// ------------------------------------------------------------------------------------------- offline and stale

#[test]
fn offline() {
    let (_t, dirs) = tmp("offline");
    let net = FakeTransport::new();
    script_glow(&net);
    let (behind, same) = (glow("1.9.0", None), glow("1.10.0", Some(GLOW_SHA)));
    let both = [behind.clone(), same.clone()];
    let first = check(&dirs, &net, &both, &opts(NOW));
    assert_eq!((first[0].state, first[1].state), (UpdateState::Available, UpdateState::Current));
    done(&net);
    let cache_after_success = std::fs::read(cache::path_of(&dirs)).unwrap();

    // offline with nothing remembered about a mod: not checked, and no request
    let none = FakeTransport::offline();
    let other = item("night-sky", "1.0.0", github("example-org/night-sky", "v{version}", None, UpdateChannel::Stable), None);
    let s = one(&dirs, &none, &other, &off(NOW + 60));
    assert_eq!((s.state, s.reason, s.stale, s.checked_at.as_ref()), (UpdateState::Unknown, Some(UnknownReason::NotChecked), false, None));
    // offline an hour later: the answers are an hour old and stand as they are
    let hour = check(&dirs, &none, &both, &off(NOW + HOUR));
    assert_eq!((hour[0].state, hour[0].stale, hour[1].state, hour[1].stale), (UpdateState::Available, false, UpdateState::Current, false));
    assert_eq!(hour[1].checked_at.as_deref(), Some("2027-01-15T08:00:00Z"), "the time of the answer, not of the offline check");
    // offline two days later: the "update available" is kept and marked stale; the "current" is not current any more
    let late = check(&dirs, &none, &both, &off(NOW + 2 * DAY));
    assert_eq!((late[0].state, late[0].stale, late[0].reason, late[0].candidate_version.as_deref()), (UpdateState::Available, true, Some(UnknownReason::Network), Some("1.10.0")));
    assert_eq!((late[1].state, late[1].stale, late[1].reason), (UpdateState::Unknown, true, Some(UnknownReason::Network)));
    assert_eq!(late[1].candidate_version.as_deref(), Some("1.10.0"), "what was seen is still shown");
    assert!(none.requests().is_empty(), "an offline check made a request: {:?}", urls(&none));
    assert_eq!(std::fs::read(cache::path_of(&dirs)).unwrap(), cache_after_success, "an offline check changes nothing it remembers");

    // online again, and the request fails (the answer is lost): the earlier success is not erased, and still nothing
    // is "current"
    let flaky = FakeTransport::new();
    flaky.reply(Method::Get, &list_url(GLOW, 1), Reply::Lost);
    let failed = check(&dirs, &flaky, &both, &opts(NOW + 2 * DAY));
    assert_eq!((failed[0].state, failed[0].stale, failed[0].reason), (UpdateState::Available, true, Some(UnknownReason::Network)));
    assert_eq!((failed[1].state, failed[1].stale, failed[1].reason), (UpdateState::Unknown, true, Some(UnknownReason::Network)));
    assert_eq!(failed[0].checked_at.as_deref(), Some("2027-01-15T08:00:00Z"));
    assert_eq!(flaky.requests().len(), 1, "one list for both mods, and no second try in the same check");
    // a failure that follows a fresh success also marks it: a later try to refresh failed
    // (the wait after the failure: 5 minutes; --refresh skips it)
    assert!(one(&dirs, &flaky, &same, &opts(NOW + 2 * DAY + 60)).stale);
    assert_eq!(flaky.requests().len(), 1, "inside the wait nothing is asked");
    flaky.reply(Method::Get, &list_url(GLOW, 1), Reply::status(500));
    let s = one(&dirs, &flaky, &same, &refresh(NOW + 2 * DAY + 61));
    assert_eq!((s.state, s.stale, flaky.requests().len()), (UpdateState::Unknown, true, 2));
    // a server error, a refused address and an answer that is no list: all the same
    flaky.reply(Method::Get, &list_url(GLOW, 1), Reply::json(200, r#"{"message": "hello"}"#));
    assert_eq!(one(&dirs, &flaky, &same, &refresh(NOW + 2 * DAY + 62)).state, UpdateState::Unknown);
    let private = FakeTransport::new();
    private.resolve("api.github.com", &["10.0.0.7"]);
    let s = one(&dirs, &private, &same, &refresh(NOW + 2 * DAY + 63));
    assert_eq!((s.state, s.reason, private.sent().len()), (UpdateState::Unknown, Some(UnknownReason::Network), 0));
    done(&flaky);

    // the provider answers again: current again, not stale
    let back = FakeTransport::new();
    back.reply(Method::Get, &list_url(GLOW, 1), Reply::status(304));
    let s = one(&dirs, &back, &same, &refresh(NOW + 2 * DAY + 120));
    assert_eq!((s.state, s.stale, s.reason), (UpdateState::Current, false, None));
    assert_eq!(back.requests()[0].header("if-none-match"), Some("W/\"glow-1\""));
    done(&back);
    println!("PASS updates.offline");
}

#[test]
fn a_304_keeps_the_last_success_and_dates_it_now() {
    let (_t, dirs) = tmp("304");
    let t = FakeTransport::new();
    script_glow(&t);
    let it = glow("1.9.0", None);
    assert_eq!(one(&dirs, &t, &it, &opts(NOW)).state, UpdateState::Available);
    // within 30 minutes a check the player started asks nothing; the daily one asks nothing within a day
    one(&dirs, &t, &it, &opts(NOW + 1700));
    one(&dirs, &t, &it, &CheckOptions { automatic: true, ..opts(NOW + DAY - 60) });
    assert_eq!(t.requests().len(), 3);
    // after that it asks, with the validators; 304: the remembered answer stands, confirmed now, with no binding read
    t.reply(Method::Get, &list_url(GLOW, 1), Reply::status(304));
    let s = one(&dirs, &t, &it, &opts(NOW + 1900));
    assert_eq!((s.state, s.stale, s.candidate_version.as_deref(), s.compatibility), (UpdateState::Available, false, Some("1.10.0"), Compatibility::Compatible));
    assert_eq!(s.checked_at.as_deref(), Some("2027-01-15T08:31:40Z"));
    let last = t.requests().pop().unwrap();
    assert_eq!((last.header("if-none-match"), last.header("if-modified-since")), (Some("W/\"glow-1\""), Some("Sun, 08 Nov 2026 09:00:00 GMT")));
    assert_eq!(t.requests().len(), 4);
    // the daily check a day after that
    t.reply(Method::Get, &list_url(GLOW, 1), Reply::status(304));
    one(&dirs, &t, &it, &CheckOptions { automatic: true, ..opts(NOW + 1900 + DAY + 1) });
    done(&t);
    // a 304 nobody asked for (no validators were sent) is not an answer
    let (_t2, dirs2) = tmp("304-unasked");
    let odd = FakeTransport::new();
    odd.reply(Method::Get, &list_url(GLOW, 1), Reply::status(304));
    let s = one(&dirs2, &odd, &it, &opts(NOW));
    assert_eq!((s.state, s.reason), (UpdateState::Unknown, Some(UnknownReason::Network)));
}

#[test]
fn a_damaged_cache_is_built_again() {
    let (_t, dirs) = tmp("damaged");
    std::fs::create_dir_all(cache::path_of(&dirs).parent().unwrap()).unwrap();
    std::fs::write(cache::path_of(&dirs), b"{\"format\": 1, \"entries\": {\"github|").unwrap();
    let t = FakeTransport::new();
    script_glow(&t);
    let it = glow("1.9.0", None);
    // offline first: nothing remembered, nothing guessed from the damaged file
    let s = one(&dirs, &FakeTransport::offline(), &it, &off(NOW));
    assert_eq!((s.state, s.reason), (UpdateState::Unknown, Some(UnknownReason::NotChecked)));
    assert_eq!(one(&dirs, &t, &it, &opts(NOW)).state, UpdateState::Available);
    let file = cache::load(&dirs);
    assert_eq!(file.entries.len(), 1);
    let e = file.entries.values().next().unwrap();
    assert_eq!((e.destination.as_str(), e.mod_id.as_str(), e.last_success, e.selection_coverage), (GLOW, "lantern-glow", Some(NOW), Some(Coverage::Complete)));
    assert_eq!((e.etag.as_deref(), e.status, e.next_allowed_at, e.receipt.version.as_str()), (Some("W/\"glow-1\""), Some(cache::Attempt::Ok), None, "1.9.0"));
    assert!(e.response_digest.as_ref().is_some_and(|d| d.len() == 64));
    done(&t);
}

#[test]
fn a_clock_that_went_backwards_makes_answers_stale_and_keeps_limits() {
    let (_t, dirs) = tmp("clock");
    let t = FakeTransport::new();
    script_glow(&t);
    let same = glow("1.10.0", Some(GLOW_SHA));
    assert_eq!(one(&dirs, &t, &same, &opts(NOW)).state, UpdateState::Current);
    // the PC's clock is two days behind the answer: the answer cannot be dated, so it is not "current"
    let s = one(&dirs, &FakeTransport::offline(), &same, &off(NOW - 2 * DAY));
    assert_eq!((s.state, s.stale), (UpdateState::Unknown, true));
    // a request limit learned at NOW (40 minutes), then the clock jumps back: still 40 minutes, counted from there
    t.reply(Method::Get, &list_url(GLOW, 1), Reply::status(403).with_header("x-ratelimit-remaining", "0").with_header("x-ratelimit-reset", &(NOW + 3 * HOUR + 2400).to_string()));
    let s = one(&dirs, &t, &same, &refresh(NOW + 3 * HOUR));
    assert_eq!((s.state, s.stale, s.reason), (UpdateState::Unknown, true, Some(UnknownReason::RateLimited)));
    let n = t.requests().len();
    let back = NOW - 2 * DAY;
    one(&dirs, &t, &same, &refresh(back));
    one(&dirs, &t, &same, &refresh(back + 2399));
    assert_eq!(t.requests().len(), n, "the limit was not lifted by the clock");
    t.reply(Method::Get, &list_url(GLOW, 1), Reply::status(304));
    let s = one(&dirs, &t, &same, &refresh(back + 2401));
    assert_eq!((s.state, s.stale, t.requests().len()), (UpdateState::Current, false, n + 1), "and it ends");
    done(&t);
}

// ------------------------------------------------------------------------------------------- limits

#[test]
fn a_list_longer_than_five_pages_is_incomplete() {
    let (_t, dirs) = tmp("pages");
    let t = FakeTransport::new();
    let repo = "example-org/many-releases";
    for page in 1..=5u64 {
        let link = format!("<https://api.github.com/repositories/1/releases?per_page=100&page={}>; rel=\"next\"", page + 1);
        t.reply(Method::Get, &list_url(repo, page as usize), Reply::json(200, &page_of(page * 1000, 100)).with_header("Link", &link));
    }
    let it = item("many", "0.1.0", github(repo, "v{version}", None, UpdateChannel::Stable), None);
    let s = one(&dirs, &t, &it, &opts(NOW));
    // a far higher version is in what was read; "newest" is still not claimed
    assert_eq!((s.state, s.reason, s.coverage, s.candidate_version.as_ref()), (UpdateState::Unknown, Some(UnknownReason::IncompleteList), Coverage::Incomplete, None));
    assert_eq!(urls(&t), (1..=5).map(|p| list_url(repo, p)).collect::<Vec<_>>(), "five pages, in order, and no sixth");
    done(&t);

    // exactly one full page and GitHub says there is no next: complete after one request
    let (_t2, dirs2) = tmp("pages-exact");
    let t = FakeTransport::new();
    t.reply(Method::Get, &list_url(repo, 1), Reply::json(200, &page_of(1000, 100)).with_header("Link", "<https://api.github.com/repositories/1/releases?per_page=100&page=1>; rel=\"first\""));
    let s = one(&dirs2, &t, &it, &opts(NOW));
    assert_eq!((s.state, s.coverage, s.candidate_version.as_deref()), (UpdateState::Available, Coverage::Complete, Some("0.1099.0")));
    done(&t);
    // a full page without a Link header: the next page is asked, and its end ends the list
    let (_t3, dirs3) = tmp("pages-nolink");
    let t = FakeTransport::new();
    t.reply(Method::Get, &list_url(repo, 1), Reply::json(200, &page_of(1000, 100)));
    t.reply(Method::Get, &list_url(repo, 2), Reply::json(200, &page_of(2000, 3)));
    let s = one(&dirs3, &t, &it, &opts(NOW));
    assert_eq!((s.state, s.coverage, s.candidate_version.as_deref()), (UpdateState::Available, Coverage::Complete, Some("0.2002.0")));
    done(&t);
    // a page over 1 MiB is not read
    let (_t4, dirs4) = tmp("pages-big");
    let t = FakeTransport::new();
    let big = Value::Array(vec![json!({"id": 1, "tag_name": "v9.0.0", "draft": false, "prerelease": false, "assets": [], "body": "x".repeat(1024 * 1024)})]).to_string();
    t.reply(Method::Get, &list_url(repo, 1), Reply::json(200, &big));
    let s = one(&dirs4, &t, &it, &opts(NOW));
    assert_eq!((s.state, s.reason), (UpdateState::Unknown, Some(UnknownReason::Network)));
}

/// A release list of one release (id 1, tag v2.0.0) with a zip and a `kiln-release.json` of the given listed size.
fn one_release(binding_size: u64) -> String {
    Value::Array(vec![release(1, "v2.0.0", &[(11, "m-2.0.0.zip", 500), (12, "kiln-release.json", binding_size)])]).to_string()
}

fn binding_for(id: &str, version: &str, name: &str, bytes: u64) -> String {
    json!({"format": 1, "mods": [{"id": id, "version": version, "side": "both", "apiVersion": "0.2", "artifact": {"name": name, "sha256": "e".repeat(64), "bytes": bytes}}]}).to_string()
}

#[test]
fn the_binding_is_bounded_and_only_read_from_githubs_asset_hosts() {
    let repo = "example-org/m";
    let it = item("m", "1.0.0", github(repo, "v{version}", Some("m-{version}.zip"), UpdateChannel::Stable), None);
    let good = binding_for("m", "2.0.0", "m-2.0.0.zip", 500);
    let run = |tag: &str, script: &dyn Fn(&FakeTransport)| {
        let (_t, dirs) = tmp(tag);
        let t = FakeTransport::new();
        t.reply(Method::Get, &list_url(repo, 1), Reply::json(200, &one_release(good.len() as u64)));
        script(&t);
        let s = one(&dirs, &t, &it, &opts(NOW));
        assert!(t.unused().is_empty(), "{tag}: {:?}", t.unused());
        (s, t)
    };
    // straight from the API host, and through one redirect to each of the two asset hosts
    let (s, _) = run("bind-direct", &|t| { t.reply(Method::Get, &asset_url(repo, "12"), Reply::bytes(200, good.as_bytes())); });
    assert_eq!((s.state, s.compatibility), (UpdateState::Available, Compatibility::Compatible));
    for host in github::ASSET_HOSTS {
        let target = format!("https://{host}/a/b?token=signed");
        let (s, t) = run("bind-redirect", &|t| {
            t.reply(Method::Get, &asset_url(repo, "12"), Reply::redirect(302, &target));
            t.reply(Method::Get, &target, Reply::bytes(200, good.as_bytes()));
        });
        assert_eq!(s.state, UpdateState::Available, "{host}");
        assert_eq!(t.requests().len(), 3);
    }
    // a redirect anywhere else is not followed: the release is not selectable
    for target in ["https://evil.example/kiln-release.json", "https://github.com/example-org/m/releases/download/v2.0.0/kiln-release.json",
        "http://objects.githubusercontent.com/a", "https://objects.githubusercontent.com.evil.example/a", "https://objects.githubusercontent.com:8443/a", "/relative"] {
        let (s, t) = run("bind-elsewhere", &|t| { t.reply(Method::Get, &asset_url(repo, "12"), Reply::redirect(302, target)); });
        assert_eq!((s.state, s.reason), (UpdateState::Unknown, Some(UnknownReason::InvalidMetadata)), "{target}");
        assert_eq!(urls(&t), [list_url(repo, 1), asset_url(repo, "12")], "{target}: nothing went to the redirect's target");
    }
    // nor a second redirect
    let hop = "https://objects.githubusercontent.com/hop";
    let (s, t) = run("bind-twice", &|t| {
        t.reply(Method::Get, &asset_url(repo, "12"), Reply::redirect(302, hop));
        t.reply(Method::Get, hop, Reply::redirect(302, "https://release-assets.githubusercontent.com/end"));
    });
    assert_eq!((s.reason, t.requests().len()), (Some(UnknownReason::InvalidMetadata), 3));
    // an answer over 256 KiB is refused, whatever size the list gave
    let huge = vec![b' '; 256 * 1024 + 1];
    let (s, _) = run("bind-huge", &|t| { t.reply(Method::Get, &asset_url(repo, "12"), Reply::bytes(200, &huge)); });
    assert_eq!((s.state, s.reason), (UpdateState::Unknown, Some(UnknownReason::InvalidMetadata)));
    // listed as over 256 KiB: not even asked for
    let (_t, dirs) = tmp("bind-listed-huge");
    let t = FakeTransport::new();
    t.reply(Method::Get, &list_url(repo, 1), Reply::json(200, &one_release(256 * 1024 + 1)));
    let s = one(&dirs, &t, &it, &opts(NOW));
    assert_eq!((s.reason, urls(&t)), (Some(UnknownReason::InvalidMetadata), vec![list_url(repo, 1)]));
    // a binding that is not valid, or is about another mod: unknown, never a candidate
    for (what, body) in [("not json", "<html>".to_string()), ("another mod", binding_for("other", "2.0.0", "m-2.0.0.zip", 500)), ("another size", binding_for("m", "2.0.0", "m-2.0.0.zip", 501))] {
        let (s, _) = run("bind-invalid", &|t| { t.reply(Method::Get, &asset_url(repo, "12"), Reply::bytes(200, body.as_bytes())); });
        assert_eq!((s.state, s.reason, s.candidate_version.as_ref()), (UpdateState::Unknown, Some(UnknownReason::InvalidMetadata), None), "{what}");
    }
    // the binding cannot be read (404, a lost answer): no new answer at all, so an earlier one would stay
    for reply in [Reply::status(404), Reply::Lost, Reply::status(500)] {
        let (s, _) = run("bind-unreadable", &|t| { t.reply(Method::Get, &asset_url(repo, "12"), reply.clone()); });
        assert_eq!((s.state, s.reason), (UpdateState::Unknown, Some(UnknownReason::Network)));
    }
}

#[test]
fn a_failed_binding_read_keeps_the_earlier_answer() {
    let (_t, dirs) = tmp("bind-keeps");
    let t = FakeTransport::new();
    script_glow(&t);
    let it = glow("1.9.0", None);
    assert_eq!(one(&dirs, &t, &it, &opts(NOW)).candidate_version.as_deref(), Some("1.10.0"));
    // the list changed (a 200), and the winner's binding cannot be read this time
    t.reply(Method::Get, &list_url(GLOW, 1), Reply::bytes(200, &fixture("lantern-glow.releases.json")).with_header("ETag", "W/\"glow-2\""));
    t.reply(Method::Get, &asset_url(GLOW, BINDING_ASSET), Reply::Lost);
    let s = one(&dirs, &t, &it, &refresh(NOW + HOUR));
    assert_eq!((s.state, s.stale, s.reason, s.candidate_version.as_deref()), (UpdateState::Available, true, Some(UnknownReason::Network), Some("1.10.0")));
    assert_eq!(s.checked_at.as_deref(), Some("2027-01-15T08:00:00Z"));
    // the validators of the answer that was not completed are not kept: the next request sends the old ones
    t.reply(Method::Get, &list_url(GLOW, 1), Reply::status(304));
    let s = one(&dirs, &t, &it, &refresh(NOW + 2 * HOUR));
    assert_eq!(t.requests().pop().unwrap().header("if-none-match"), Some("W/\"glow-1\""));
    assert_eq!((s.state, s.stale), (UpdateState::Available, false));
    done(&t);
}

#[test]
fn a_request_limit_stops_every_request_until_it_ends_and_refresh_does_not_lift_it() {
    let (_t, dirs) = tmp("limit");
    let t = FakeTransport::new();
    let (a, b) = (glow("1.9.0", None), item("builds", "1.0.0", github("example-org/build-tags", "v{version}", None, UpdateChannel::Stable), None));
    let reset = NOW + 1500;
    t.reply(Method::Get, &list_url(GLOW, 1), Reply::json(403, r#"{"message": "API rate limit exceeded"}"#).with_header("x-ratelimit-remaining", "0").with_header("x-ratelimit-reset", &reset.to_string()));
    let s = check(&dirs, &t, &[a.clone(), b.clone()], &opts(NOW));
    // the first mod hit the limit; the second was not asked about at all, and says why
    assert_eq!((s[0].state, s[0].reason, s[1].state, s[1].reason), (UpdateState::Unknown, Some(UnknownReason::RateLimited), UpdateState::Unknown, Some(UnknownReason::RateLimited)));
    assert_eq!(urls(&t), [list_url(GLOW, 1)]);
    // --refresh, a new provider (another start of the program), a third mod: nothing is asked while the limit runs
    for (now, o) in [(NOW + 1, refresh(NOW + 1)), (NOW + 600, refresh(NOW + 600)), (NOW + 1499, opts(NOW + 1499))] {
        let s = check(&dirs, &t, &[a.clone(), b.clone()], &o);
        assert_eq!((s[0].reason, s[1].reason), (Some(UnknownReason::RateLimited), Some(UnknownReason::RateLimited)), "{now}");
    }
    assert_eq!(t.requests().len(), 1, "{:?}", urls(&t));
    // at its end the checks go on
    script_glow(&t);
    t.reply(Method::Get, &list_url("example-org/build-tags", 1), Reply::bytes(200, &fixture("build-tags.releases.json")));
    let s = check(&dirs, &t, &[a.clone(), b.clone()], &opts(reset));
    assert_eq!((s[0].state, s[1].state, s[1].reason), (UpdateState::Available, UpdateState::Unknown, Some(UnknownReason::AmbiguousRelease)));
    done(&t);

    // 429 with Retry-After, after a success: the success is kept, stale
    t.reply(Method::Get, &list_url(GLOW, 1), Reply::status(429).with_header("Retry-After", "120"));
    let s = one(&dirs, &t, &a, &refresh(reset + HOUR));
    assert_eq!((s.state, s.stale, s.reason), (UpdateState::Available, true, Some(UnknownReason::RateLimited)));
    let n = t.requests().len();
    one(&dirs, &t, &a, &refresh(reset + HOUR + 119));
    assert_eq!(t.requests().len(), n);
    t.reply(Method::Get, &list_url(GLOW, 1), Reply::status(304));
    assert!(!one(&dirs, &t, &a, &refresh(reset + HOUR + 120)).stale);
    // an answer that used the last request of the budget: the next source waits for the reset, unasked
    let (_t2, dirs2) = tmp("limit-budget");
    let t = FakeTransport::new();
    t.reply(Method::Get, &list_url("example-org/build-tags", 1), Reply::bytes(200, &fixture("build-tags.releases.json")).with_header("x-ratelimit-remaining", "0").with_header("x-ratelimit-reset", &(NOW + 900).to_string()));
    let s = check(&dirs2, &t, &[b.clone(), a.clone()], &opts(NOW));
    assert_eq!((s[0].reason, s[1].reason), (Some(UnknownReason::AmbiguousRelease), Some(UnknownReason::RateLimited)));
    assert_eq!(t.requests().len(), 1);
    // a 403 that is no limit (no limit headers) and a 404: not a limit; the next source is still asked
    let (_t3, dirs3) = tmp("limit-not");
    let t = FakeTransport::new();
    t.reply(Method::Get, &list_url(GLOW, 1), Reply::status(403));
    t.reply(Method::Get, &list_url("example-org/build-tags", 1), Reply::status(404));
    let s = check(&dirs3, &t, &[a, b], &opts(NOW));
    assert_eq!((s[0].reason, s[1].reason), (Some(UnknownReason::Network), Some(UnknownReason::NotFound)));
    done(&t);
}

#[test]
fn a_repository_of_several_mods_is_listed_once_and_five_hundred_sources_are_the_most() {
    let (_t, dirs) = tmp("mono");
    let t = FakeTransport::new();
    let repo = "example-org/kiln-mods";
    t.reply(Method::Get, &list_url(repo, 1), Reply::bytes(200, &fixture("kiln-mods.releases.json")));
    let items = [
        item("example", "1.4.0", github(repo, "mods/example/v{version}", Some("example-{version}.zip"), UpdateChannel::Stable), None),
        item("other", "9.0.0", github("Example-Org/Kiln-Mods", "mods/other/v{version}", None, UpdateChannel::Stable), None),
        item("extras", "8.0.0", github(repo, "mods/example-extras/v{version}", None, UpdateChannel::Prerelease), None),
    ];
    let s = check(&dirs, &t, &items, &opts(NOW));
    assert_eq!((s[0].state, s[0].candidate_version.as_deref()), (UpdateState::Available, Some("1.5.0")));
    assert_eq!((s[1].state, s[1].reason), (UpdateState::Unknown, Some(UnknownReason::SameVersionUnbound)));
    assert_eq!(s[2].candidate_version.as_deref(), Some("8.0.0"));
    assert_eq!(t.requests().len(), 1, "{:?}", urls(&t));
    assert_eq!(cache::load(&dirs).entries.len(), 3, "one entry per mod and source");
    done(&t);

    // 501 sources in one check: 500 are asked, the last one is told it was not
    let (_t2, dirs2) = tmp("many");
    let t = FakeTransport::new();
    let many: Vec<CheckItem> = (0..501).map(|i| item(&format!("m{i}"), "1.0.0", github(&format!("example-org/repo-{i}"), "v{version}", None, UpdateChannel::Stable), None)).collect();
    for i in 0..500 {
        t.reply(Method::Get, &list_url(&format!("example-org/repo-{i}"), 1), Reply::json(200, "[]"));
    }
    let s = check(&dirs2, &t, &many, &opts(NOW));
    assert!(s[..500].iter().all(|x| x.reason == Some(UnknownReason::NotFound)));
    assert_eq!((s[500].state, s[500].reason), (UpdateState::Unknown, Some(UnknownReason::NotChecked)));
    assert_eq!(t.requests().len(), 500);
    done(&t);
}

// ------------------------------------------------------------------------------------------- no download, no install

/// Every file under a folder, relative, sorted.
fn files_under(root: &Path) -> Vec<String> {
    fn walk(dir: &Path, root: &Path, out: &mut Vec<String>) {
        for e in std::fs::read_dir(dir).into_iter().flatten().flatten() {
            if e.path().is_dir() { walk(&e.path(), root, out) } else { out.push(e.path().strip_prefix(root).unwrap().to_string_lossy().replace('\\', "/")) }
        }
    }
    let mut out = Vec::new();
    walk(root, root, &mut out);
    out.sort();
    out
}

#[test]
fn no_auto_install() {
    let (_t, dirs) = tmp("no-install");
    let t = FakeTransport::new();
    script_glow(&t);
    t.reply(Method::Get, &list_url("example-org/kiln-mods", 1), Reply::bytes(200, &fixture("kiln-mods.releases.json")));
    let items = [
        glow("1.9.0", Some(&"5".repeat(64))),
        item("example", "1.4.0", github("example-org/kiln-mods", "mods/example/v{version}", Some("example-{version}.zip"), UpdateChannel::Stable), None),
    ];
    // an update is available for both, on every kind of check
    for o in [opts(NOW), CheckOptions { automatic: true, ..opts(NOW + 60) }, off(NOW + 120)] {
        let s = check(&dirs, &t, &items, &o);
        assert_eq!((s[0].state, s[1].state), (UpdateState::Available, UpdateState::Available));
    }
    t.reply(Method::Get, &list_url(GLOW, 1), Reply::status(304));
    t.reply(Method::Get, &list_url("example-org/kiln-mods", 1), Reply::bytes(200, &fixture("kiln-mods.releases.json")));
    check(&dirs, &t, &items, &refresh(NOW + 180));
    done(&t);
    // not one request for a mod's file: the only asset ever asked for is the candidate's kiln-release.json, and no
    // download address of the list (browser_download_url, the asset's own url) was followed
    let zips = [ZIP_ASSET, "7141", "7161", "7031", "7061", "7071"];
    for r in t.requests() {
        assert!(!r.url.contains("/releases/download/"), "{}", r.url);
        assert!(!r.url.ends_with(".zip") && !r.url.contains(".zip?"), "{}", r.url);
        if let Some(id) = r.url.strip_prefix(&format!("https://api.github.com/repos/{GLOW}/releases/assets/")) {
            assert_eq!(id, BINDING_ASSET, "an asset other than the binding was requested");
        }
        for z in zips {
            assert!(!r.url.ends_with(&format!("/assets/{z}")), "{}", r.url);
        }
        assert!(r.url.contains("/releases?per_page=100&page=") || r.url.ends_with(&format!("/assets/{BINDING_ASSET}")) || r.host == "release-assets.githubusercontent.com", "{}", r.url);
    }
    // and nothing was put anywhere: the data folder holds the check's own two files, no library, no profile, no download
    assert_eq!(files_under(&dirs.root), ["updates/.lock", "updates/cache.json"]);
    // the only thing a status offers is a page to open, on github.com
    for s in check(&dirs, &FakeTransport::offline(), &items, &off(NOW + 240)) {
        assert!(s.page_url.as_deref().is_some_and(|u| u.starts_with("https://github.com/example-org/") && u.contains("/releases/tag/")), "{:?}", s.page_url);
    }
    println!("PASS updates.no-auto-install (provider)");
}

// ------------------------------------------------------------------------------------------- concurrency, cancel

/// A transport that holds every exchange until it is released, and counts them.
struct Gate<'a> {
    inner: &'a FakeTransport,
    open: std::sync::Mutex<bool>,
    changed: std::sync::Condvar,
    entered: std::sync::atomic::AtomicUsize,
}

impl Transport for Gate<'_> {
    fn exchange(&self, req: WireRequest) -> kiln_manage::Result<Response> {
        self.entered.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
        let mut open = self.open.lock().unwrap();
        while !*open {
            open = self.changed.wait(open).unwrap();
        }
        drop(open);
        self.inner.exchange(req)
    }
}

#[test]
fn two_checks_at_once_ask_for_a_list_once() {
    let (_t, dirs) = tmp("inflight");
    let fake = FakeTransport::new();
    script_glow(&fake);
    // the list is what is shared; each check still reads the winner's small binding for itself
    fake.reply(Method::Get, &asset_url(GLOW, BINDING_ASSET), Reply::bytes(200, &fixture("lantern-glow.kiln-release.json")));
    let gate = Gate { inner: &fake, open: std::sync::Mutex::new(false), changed: std::sync::Condvar::new(), entered: Default::default() };
    let provider = GithubProvider::new(&dirs, &gate, &github::user_agent());
    let it = glow("1.9.0", None);
    let o = refresh(NOW);
    let states: Vec<UpdateState> = std::thread::scope(|scope| {
        let run = || provider.check(std::slice::from_ref(&it), &o, &mut NoProgress).unwrap().remove(0).state;
        let first = scope.spawn(run);
        // the first check is inside its request before the second starts
        while gate.entered.load(std::sync::atomic::Ordering::SeqCst) == 0 {
            std::thread::yield_now();
        }
        let second = scope.spawn(run);
        // give the second one time to reach the list (it must wait there, not ask); then let the answers through
        std::thread::sleep(std::time::Duration::from_millis(300));
        assert_eq!(gate.entered.load(std::sync::atomic::Ordering::SeqCst), 1, "the second check asked for the list while the first was reading it");
        *gate.open.lock().unwrap() = true;
        gate.changed.notify_all();
        vec![first.join().unwrap(), second.join().unwrap()]
    });
    assert_eq!(states, [UpdateState::Available, UpdateState::Available]);
    let lists = urls(&fake).iter().filter(|u| u.contains("/releases?per_page")).count();
    assert_eq!(lists, 1, "{:?}", urls(&fake));
    assert!(fake.unexpected().is_empty(), "{:?}", fake.unexpected());
}

struct CancelAfter(std::cell::Cell<u32>);
impl Progress for CancelAfter {
    fn report(&mut self, _: Option<f32>, _: &str) {}
    fn canceled(&self) -> bool {
        let left = self.0.get();
        self.0.set(left.saturating_sub(1));
        left == 0
    }
}

#[test]
fn a_canceled_check_stops_asking_and_keeps_what_it_learned() {
    let (_t, dirs) = tmp("cancel");
    let t = FakeTransport::new();
    t.reply(Method::Get, &list_url("example-org/build-tags", 1), Reply::bytes(200, &fixture("build-tags.releases.json")));
    let items = [item("builds", "1.0.0", github("example-org/build-tags", "v{version}", None, UpdateChannel::Stable), None), glow("1.9.0", None)];
    // canceled() is polled once per mod and once per page: the third poll (before the second mod) says stop
    let e = GithubProvider::new(&dirs, &t, &github::user_agent()).check(&items, &opts(NOW), &mut CancelAfter(std::cell::Cell::new(2))).unwrap_err();
    assert_eq!(e.code, ErrorCode::Canceled);
    assert_eq!(urls(&t), [list_url("example-org/build-tags", 1)], "the second mod was not asked about");
    assert_eq!(cache::load(&dirs).entries.len(), 1, "the first mod's answer is remembered");
    // canceled in the middle of a list: nothing of it is remembered
    let (_t2, dirs2) = tmp("cancel-pages");
    let t = FakeTransport::new();
    t.reply(Method::Get, &list_url("example-org/many", 1), Reply::json(200, &page_of(1000, 100)));
    let it = item("many", "0.1.0", github("example-org/many", "v{version}", None, UpdateChannel::Stable), None);
    let e = GithubProvider::new(&dirs2, &t, &github::user_agent()).check(std::slice::from_ref(&it), &opts(NOW), &mut CancelAfter(std::cell::Cell::new(2))).unwrap_err();
    assert_eq!((e.code, t.requests().len()), (ErrorCode::Canceled, 1));
    assert!(cache::load(&dirs2).entries.is_empty());
}
