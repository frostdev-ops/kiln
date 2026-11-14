//! The GitHub provider against a fake transport. The answers are written by hand from GitHub's public REST
//! documentation (shapes and status codes), with made-up repository names and ids: nothing was recorded from a real
//! repository and no test sends a request. Every test ends by looking at the request log.

use super::*;
use crate::net::fake::{FakeTransport, Recorded, Reply};
use crate::publish::engine::{no_faults, Engine, OutcomeState, Progress};
use crate::publish::journal::{StepState, PUBLISH_STEP};
use crate::publish::plan::{create, Loaded, PlanRequest};
use std::io::Write;
use std::path::PathBuf;

const TOKEN: &str = "ghp_FAKETOKENFAKETOKEN0123456789";
const COMMIT: &str = "0123456789abcdef0123456789abcdef01234567";
const LIST: &str = "https://api.github.com/repos/example/good-mod/releases?per_page=100&page=1";
const CREATE: &str = "https://api.github.com/repos/example/good-mod/releases";
const RELEASE: &str = "https://api.github.com/repos/example/good-mod/releases/501";
const ASSETS: &str = "https://api.github.com/repos/example/good-mod/releases/501/assets?per_page=100";
const TAG_REF: &str = "https://api.github.com/repos/example/good-mod/git/ref/tags/v1.0.0";
/// The download address of asset 900, and where GitHub's answer to it may send us (signed, as GitHub's storage
/// addresses are).
const ASSET_900: &str = "https://api.github.com/repos/example/good-mod/releases/assets/900";
const STORAGE_900: &str = "https://release-assets.githubusercontent.com/github-production-release-asset/900?sp=r&sv=2018-11-09&sig=SIGNEDSECRET";

struct Tok;
impl TokenSource for Tok {
    fn token(&self) -> Result<String> {
        Ok(TOKEN.into())
    }
}

struct Fx {
    root: PathBuf,
    loaded: Loaded,
}
impl Drop for Fx {
    fn drop(&mut self) {
        txdir::discard(&self.loaded.dir);
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

fn fx() -> Fx {
    let root = std::env::temp_dir().join(format!("kiln-publish-github-{}", crate::util::uuid_v4().unwrap()));
    std::fs::create_dir_all(&root).unwrap();
    let zip = root.join("m.zip");
    let mut w = zip::ZipWriter::new(std::fs::File::create(&zip).unwrap());
    w.start_file("good-mod/kiln.mod.json", zip::write::SimpleFileOptions::default()).unwrap();
    w.write_all(br#"{"id": "good-mod", "version": "1.0.0", "apiVersion": "0.2"}"#).unwrap();
    w.finish().unwrap();
    std::fs::write(root.join("notes.md"), "First release.\n").unwrap();
    let config = root.join("kiln.publish.json");
    std::fs::write(&config, format!(r#"{{"format": 1, "provider": "github", "github": {{"repository": "example/good-mod", "tag": "v{{version}}", "commit": "{COMMIT}"}}, "notes": "notes.md"}}"#)).unwrap();
    let data = root.join("data");
    let p = create(&PlanRequest { data_root: &data, artifact: &zip, config: &config, provider: "github", detached: false }).unwrap();
    let loaded = plan::load(&data, &p.plan).unwrap();
    Fx { root, loaded }
}

fn release_json(f: &Fx, id: u64, draft: bool, marker: Option<&str>) -> String {
    let p = &f.loaded.plan;
    let marker = marker.map(str::to_string).unwrap_or_else(|| plan::body_marker(&p.tx_id, &p.digest));
    serde_json::json!({"id": id, "tag_name": p.destination.tag, "target_commitish": p.target_commit, "draft": draft, "prerelease": false,
        "name": "good-mod 1.0.0", "body": format!("First release.\n\n{marker}"), "upload_url": "https://uploads.github.com/repos/example/good-mod/releases/501/assets{?name,label}"}).to_string()
}

fn upload_url(name: &str) -> String {
    format!("https://uploads.github.com/repos/example/good-mod/releases/501/assets?name={name}")
}

fn asset_json(id: u64, name: &str, file: &plan::FrozenFile, digest: bool) -> String {
    let mut v = serde_json::json!({"id": id, "name": name, "size": file.bytes, "state": "uploaded"});
    if digest {
        v["digest"] = format!("sha256:{}", file.sha256).into();
    }
    v.to_string()
}

fn assets(f: &Fx) -> Vec<(String, plan::FrozenFile)> {
    let p = &f.loaded.plan;
    vec![("good-mod-1.0.0.zip".to_string(), p.files.artifact.clone()), ("good-mod-1.0.0.zip.sha256".to_string(), p.files.checksum.clone()), ("kiln-release.json".to_string(), p.files.release.clone())]
}

/// Scripts the answers of a whole clean apply.
fn script_clean(f: &Fx, t: &FakeTransport) {
    t.reply(Method::Get, TAG_REF, Reply::status(404));
    t.reply(Method::Get, LIST, Reply::json(200, "[]"));
    t.reply(Method::Post, CREATE, Reply::json(201, &release_json(f, 501, true, None)));
    for (i, (name, file)) in assets(f).iter().enumerate() {
        t.reply(Method::Get, RELEASE, Reply::json(200, &release_json(&f, 501, true, None)));
        t.reply(Method::Post, &upload_url(name), Reply::json(201, &asset_json(900 + i as u64, name, file, true)));
    }
}

fn apply(f: &Fx, t: &FakeTransport) -> crate::publish::engine::PublishOutcome {
    let provider = GithubProvider::new(t, &Tok, "kiln-test/0");
    let mut sink = |_: &Progress| {};
    Engine { provider: &provider, events: &mut sink, fault: &no_faults }.apply(&f.loaded).unwrap()
}

fn publish(f: &Fx, t: &FakeTransport) -> crate::publish::engine::PublishOutcome {
    let provider = GithubProvider::new(t, &Tok, "kiln-test/0");
    let mut sink = |_: &Progress| {};
    Engine { provider: &provider, events: &mut sink, fault: &no_faults }.publish_draft(&f.loaded).unwrap()
}

/// What must hold for every request any test made.
fn check_log(t: &FakeTransport) -> Vec<Recorded> {
    let log = t.requests();
    for r in &log {
        // the one redirect hop of an asset download: GitHub's storage, a GET, no credential of any kind
        if ASSET_HOSTS.contains(&r.host.as_str()) {
            assert_eq!(r.method, Method::Get);
            assert_eq!(r.header("authorization"), None, "the token went to {}", r.host);
            assert!(r.headers.iter().all(|(k, v)| !v.contains(TOKEN) && !crate::net::policy::is_credential_header(k)), "a credential went to {}", r.host);
            continue;
        }
        assert!(r.host == API_HOST || r.host == UPLOAD_HOST, "a request went to {}", r.host);
        assert_eq!(r.header("authorization"), Some(format!("Bearer {TOKEN}").as_str()));
        assert!(matches!(r.method, Method::Get | Method::Post | Method::Patch), "{:?}", r.method);
        assert!(r.method == Method::Get || (!r.url.contains("/git/") && !r.url.contains("/tags")), "a tag was mutated: {}", r.url);
    }
    // a PATCH is only ever the publish of our own release, and says nothing but draft:false
    for r in log.iter().filter(|r| r.method == Method::Patch) {
        assert_eq!((r.url.as_str(), r.body.as_slice()), (RELEASE, &br#"{"draft":false}"#[..]));
    }
    assert!(t.unexpected().is_empty(), "unscripted requests: {:?}", t.unexpected());
    log
}

#[test]
fn a_clean_apply_makes_one_draft_and_three_assets_and_does_not_publish() {
    let f = fx();
    let t = FakeTransport::new();
    script_clean(&f, &t);
    let o = apply(&f, &t);
    assert!(o.ok);
    assert_eq!(o.state, OutcomeState::DraftReady);
    assert_eq!(o.remote_ids["draft"]["release"], "501");
    assert_eq!(o.remote_ids["asset:good-mod-1.0.0.zip"]["asset"], "900");
    let log = check_log(&t);
    assert!(t.unused().is_empty(), "{:?}", t.unused());
    assert_eq!(log.iter().filter(|r| r.method == Method::Post && r.url == CREATE).count(), 1);
    // the draft: pinned tag and commit, draft true, the notes and the marker at the end of the body
    let create = log.iter().find(|r| r.url == CREATE).unwrap();
    let body: Value = serde_json::from_slice(&create.body).unwrap();
    let p = &f.loaded.plan;
    assert_eq!((body["tag_name"].as_str(), body["target_commitish"].as_str(), body["draft"].as_bool()), (Some("v1.0.0"), Some(COMMIT), Some(true)));
    assert_eq!(body["body"].as_str().unwrap(), format!("First release.\n\n{}", plan::body_marker(&p.tx_id, &p.digest)));
    // each asset: the frozen file's bytes, streamed whole
    for (name, file) in assets(&f) {
        let up = log.iter().find(|r| r.url == upload_url(&name)).unwrap();
        assert_eq!((up.body_len, up.body_sha256.as_str(), up.header("content-type")), (file.bytes, file.sha256.as_str(), Some("application/octet-stream")), "{name}");
    }
    assert!(log.iter().all(|r| r.method != Method::Patch));
    println!("PASS publish.github-fixtures (clean apply)");
}

#[test]
fn a_tag_that_has_a_release_or_a_foreign_draft_is_refused_before_anything_is_created() {
    for (what, existing) in [("published release", (false, Some("")) as (bool, Option<&str>)), ("foreign draft", (true, Some("<!-- kiln-publish:v1 tx=someone-else plan=0000 -->"))), ("plain draft", (true, Some("hand-made")))] {
        let f = fx();
        let t = FakeTransport::new();
        t.reply(Method::Get, LIST, Reply::json(200, &format!("[{}]", release_json(&f, 77, existing.0, existing.1))));
        let o = apply(&f, &t);
        assert_eq!(o.state, OutcomeState::Refused, "{what}");
        assert!(o.reason.as_deref().unwrap().contains("did not make"), "{what}: {:?}", o.reason);
        let log = check_log(&t);
        assert!(log.iter().all(|r| r.method == Method::Get), "{what}: something was sent besides the lookup");
    }
}

#[test]
fn a_lost_answer_to_the_draft_adopts_the_one_draft_with_our_marker() {
    let f = fx();
    let t = FakeTransport::new();
    t.reply(Method::Get, TAG_REF, Reply::status(404));
    t.reply(Method::Get, LIST, Reply::json(200, "[]"));
    t.reply(Method::Post, CREATE, Reply::Lost);
    t.reply(Method::Get, LIST, Reply::json(200, &format!("[{}]", release_json(&f, 501, true, None))));
    for (i, (name, file)) in assets(&f).iter().enumerate() {
        t.reply(Method::Get, RELEASE, Reply::json(200, &release_json(&f, 501, true, None)));
        t.reply(Method::Post, &upload_url(name), Reply::json(201, &asset_json(900 + i as u64, name, file, true)));
    }
    let o = apply(&f, &t);
    assert_eq!(o.state, OutcomeState::DraftReady);
    assert_eq!(check_log(&t).iter().filter(|r| r.url == CREATE).count(), 1, "the draft was created twice");

    // the answer was lost and nothing arrived: one more attempt, then the draft exists once
    let f = fx();
    let t = FakeTransport::new();
    t.reply(Method::Get, TAG_REF, Reply::status(404));
    t.reply(Method::Get, LIST, Reply::json(200, "[]")).reply(Method::Post, CREATE, Reply::Lost);
    t.reply(Method::Get, TAG_REF, Reply::status(404));
    t.reply(Method::Get, LIST, Reply::json(200, "[]")); // reconcile: not there
    t.reply(Method::Get, TAG_REF, Reply::status(404));
    t.reply(Method::Get, LIST, Reply::json(200, "[]")); // the second attempt's own look
    t.reply(Method::Post, CREATE, Reply::json(201, &release_json(&f, 501, true, None)));
    for (i, (name, file)) in assets(&f).iter().enumerate() {
        t.reply(Method::Get, RELEASE, Reply::json(200, &release_json(&f, 501, true, None)));
        t.reply(Method::Post, &upload_url(name), Reply::json(201, &asset_json(900 + i as u64, name, file, true)));
    }
    assert_eq!(apply(&f, &t).state, OutcomeState::DraftReady);
    check_log(&t);

    // two drafts with our marker, or a list that cannot be read: a person has to look
    let f = fx();
    let t = FakeTransport::new();
    t.reply(Method::Get, TAG_REF, Reply::status(404));
    t.reply(Method::Get, LIST, Reply::json(200, "[]")).reply(Method::Post, CREATE, Reply::Lost);
    t.reply(Method::Get, LIST, Reply::json(200, &format!("[{},{}]", release_json(&f, 501, true, None), release_json(&f, 502, true, None))));
    let o = apply(&f, &t);
    assert_eq!(o.state, OutcomeState::ManualReconcile);
    assert!(o.manual_steps[0].contains("kiln-publish:v1"));
    check_log(&t);
}

#[test]
fn uploads_are_reconciled_by_name_size_and_digest() {
    let script_until_first_upload = |f: &Fx, t: &FakeTransport| {
        t.reply(Method::Get, TAG_REF, Reply::status(404));
    t.reply(Method::Get, LIST, Reply::json(200, "[]"));
        t.reply(Method::Post, CREATE, Reply::json(201, &release_json(f, 501, true, None)));
    };
    // lost answer, the asset is there with our digest: adopted, not uploaded again
    let f = fx();
    let t = FakeTransport::new();
    script_until_first_upload(&f, &t);
    let all = assets(&f);
    t.reply(Method::Get, RELEASE, Reply::json(200, &release_json(&f, 501, true, None)));
    t.reply(Method::Post, &upload_url(&all[0].0), Reply::Lost);
    t.reply(Method::Get, ASSETS, Reply::json(200, &format!("[{}]", asset_json(900, &all[0].0, &all[0].1, true))));
    for (i, (name, file)) in all.iter().enumerate().skip(1) {
        t.reply(Method::Get, RELEASE, Reply::json(200, &release_json(&f, 501, true, None)));
        t.reply(Method::Post, &upload_url(name), Reply::json(201, &asset_json(900 + i as u64, name, file, true)));
    }
    assert_eq!(apply(&f, &t).state, OutcomeState::DraftReady);
    assert_eq!(check_log(&t).iter().filter(|r| r.url == upload_url(&all[0].0)).count(), 1);

    // a partial upload holds the name; same name with another size; same size with another digest (no digest: the
    // tests below, the asset is downloaded and hashed)
    let partial = serde_json::json!({"id": 900, "name": all[0].0, "size": 0, "state": "starter"}).to_string();
    for what in ["partial", "size", "digest"] {
        let f = fx();
        let t = FakeTransport::new();
        script_until_first_upload(&f, &t);
        let name = &assets(&f)[0].0;
        t.reply(Method::Get, RELEASE, Reply::json(200, &release_json(&f, 501, true, None)));
        t.reply(Method::Post, &upload_url(name), Reply::Lost);
        let listed = match what {
            "size" => asset_json(900, name, &plan::FrozenFile { bytes: f.loaded.plan.files.artifact.bytes + 1, ..f.loaded.plan.files.artifact.clone() }, true),
            "digest" => asset_json(900, name, &plan::FrozenFile { sha256: "f".repeat(64), ..f.loaded.plan.files.artifact.clone() }, true),
            _ => partial.clone(),
        };
        t.reply(Method::Get, ASSETS, Reply::json(200, &format!("[{listed}]")));
        let o = apply(&f, &t);
        assert_eq!(o.state, OutcomeState::ManualReconcile, "{what}");
        assert_eq!(o.confirmed_steps, ["draft"], "{what}");
        let log = check_log(&t);
        assert_eq!(log.iter().filter(|r| r.host == UPLOAD_HOST).count(), 1, "{what}: uploaded again over something that was there");
    }
}

#[test]
fn refusals_say_why_and_change_nothing() {
    let cases: Vec<(Reply, &str)> = vec![
        (Reply::json(401, r#"{"message": "Bad credentials"}"#), "401"),
        (Reply::json(403, r#"{"message": "Resource not accessible by personal access token"}"#), "403"),
        (Reply::json(404, r#"{"message": "Not Found"}"#), "404"),
        (Reply::json(422, r#"{"message": "Validation Failed", "errors": [{"code": "already_exists"}]}"#), "422"),
        (Reply::json(409, r#"{"message": "Conflict"}"#), "409"),
        (Reply::json(403, r#"{"message": "You have exceeded a secondary rate limit"}"#).with_header("Retry-After", "60"), "retry after 60"),
        (Reply::json(429, "{}").with_header("Retry-After", "30"), "retry after 30"),
        (Reply::json(403, "{}").with_header("x-ratelimit-remaining", "0"), "rate limit"),
    ];
    for (reply, needle) in cases {
        let f = fx();
        let t = FakeTransport::new();
        t.reply(Method::Get, TAG_REF, Reply::status(404));
    t.reply(Method::Get, LIST, Reply::json(200, "[]"));
        t.reply(Method::Post, CREATE, reply);
        let o = apply(&f, &t);
        assert_eq!(o.state, OutcomeState::Refused, "{needle}");
        let why = o.reason.unwrap();
        assert!(why.contains(needle), "{needle}: {why}");
        assert!(!why.contains(TOKEN));
        check_log(&t);
    }
    // a refusal in the middle keeps the draft and resumes later
    let f = fx();
    let t = FakeTransport::new();
    t.reply(Method::Get, TAG_REF, Reply::status(404));
    t.reply(Method::Get, LIST, Reply::json(200, "[]"));
    t.reply(Method::Post, CREATE, Reply::json(201, &release_json(&f, 501, true, None)));
    let all = assets(&f);
    t.reply(Method::Get, RELEASE, Reply::json(200, &release_json(&f, 501, true, None)));
    t.reply(Method::Post, &upload_url(&all[0].0), Reply::json(403, "{}").with_header("Retry-After", "5"));
    let o = apply(&f, &t);
    assert_eq!((o.state, o.confirmed_steps.clone()), (OutcomeState::Partial, vec!["draft".to_string()]));
    t.reply(Method::Get, ASSETS, Reply::json(200, "[]"));
    for (i, (name, file)) in all.iter().enumerate() {
        t.reply(Method::Get, RELEASE, Reply::json(200, &release_json(&f, 501, true, None)));
        t.reply(Method::Post, &upload_url(name), Reply::json(201, &asset_json(900 + i as u64, name, file, true)));
    }
    assert_eq!(apply(&f, &t).state, OutcomeState::DraftReady);
    assert_eq!(check_log(&t).iter().filter(|r| r.url == CREATE).count(), 1);
    // a server error is not a refusal: the outcome is unknown and is looked up
    let f = fx();
    let t = FakeTransport::new();
    t.reply(Method::Get, TAG_REF, Reply::status(404));
    t.reply(Method::Get, LIST, Reply::json(200, "[]")).reply(Method::Post, CREATE, Reply::status(502));
    t.reply(Method::Get, LIST, Reply::status(502));
    assert_eq!(apply(&f, &t).state, OutcomeState::ManualReconcile);
    check_log(&t);
}

#[test]
fn publishing_is_separate_checks_the_marker_and_survives_a_lost_answer() {
    let f = fx();
    let t = FakeTransport::new();
    script_clean(&f, &t);
    apply(&f, &t);
    // the release was replaced by something without our marker: not published
    t.reply(Method::Get, RELEASE, Reply::json(200, &release_json(&f, 501, true, Some("edited by hand"))));
    let o = publish(&f, &t);
    assert!(!o.ok);
    assert!(o.reason.as_deref().unwrap().contains("marker"));
    assert!(check_log(&t).iter().all(|r| r.method != Method::Patch));
    // still our draft: published; the answer is lost; the lookup says it is public; nothing is sent twice
    t.reply(Method::Get, RELEASE, Reply::json(200, &release_json(&f, 501, true, None)));
    t.reply(Method::Get, TAG_REF, Reply::status(404));
    t.reply(Method::Get, RELEASE, Reply::json(200, &release_json(&f, 501, true, None)));
    t.reply(Method::Patch, RELEASE, Reply::Lost);
    t.reply(Method::Get, RELEASE, Reply::json(200, &release_json(&f, 501, false, None)));
    t.reply(Method::Get, TAG_REF, Reply::status(404));
    let o = publish(&f, &t);
    assert!(o.ok);
    assert_eq!(o.state, OutcomeState::Published);
    assert_eq!(check_log(&t).iter().filter(|r| r.method == Method::Patch).count(), 1);
    // again: already public, no request that changes anything
    let o = publish(&f, &t);
    assert_eq!(o.state, OutcomeState::Published);
    assert_eq!(check_log(&t).iter().filter(|r| r.method == Method::Patch).count(), 1);
    let j: crate::publish::journal::Journal = crate::util::read_json(&f.loaded.dir.join(txdir::JOURNAL)).unwrap().unwrap();
    assert_eq!(j.steps.iter().find(|s| s.id == PUBLISH_STEP).unwrap().state, StepState::Confirmed);
}

#[test]
fn nothing_written_or_reported_holds_the_token_and_the_token_goes_to_github_only() {
    let f = fx();
    let t = FakeTransport::new();
    t.reply(Method::Get, TAG_REF, Reply::status(404));
    t.reply(Method::Get, LIST, Reply::json(200, "[]"));
    t.reply(Method::Post, CREATE, Reply::json(201, &release_json(&f, 501, true, None)));
    let all = assets(&f);
    t.reply(Method::Get, RELEASE, Reply::json(200, &release_json(&f, 501, true, None)));
    t.reply(Method::Post, &upload_url(&all[0].0), Reply::Lost);
    t.reply(Method::Get, ASSETS, Reply::json(401, &format!(r#"{{"message": "Bad credentials for {TOKEN}"}}"#)));
    let provider = GithubProvider::new(&t, &Tok, "kiln-test/0");
    let mut events = Vec::new();
    let mut sink = |p: &Progress| events.push(p.clone());
    let o = Engine { provider: &provider, events: &mut sink, fault: &no_faults }.apply(&f.loaded).unwrap();
    assert_eq!(o.state, OutcomeState::ManualReconcile);
    let mut text = serde_json::to_string(&events).unwrap() + &serde_json::to_string(&o).unwrap();
    for e in std::fs::read_dir(&f.loaded.dir).unwrap().flatten() {
        if e.file_name() != txdir::ARTIFACT {
            text += &String::from_utf8_lossy(&std::fs::read(e.path()).unwrap());
        }
    }
    for needle in [TOKEN, "ghp_", "Bearer"] {
        assert!(!text.contains(needle), "{needle} was written or reported");
    }
    check_log(&t);
    // the policy itself: the token cannot go elsewhere, also when a caller tries
    let client = Client::new(policy(), &t, "kiln-test/0");
    let e = client.send(Request::get("https://objects.githubusercontent.com/x").header("Authorization", TOKEN)).unwrap_err();
    assert_eq!(e.code, crate::ErrorCode::Refused);
    println!("PASS publish.no-credential-leak");
    println!("PASS publish.github-fixtures");
}

/// The same with the token coming from the environment fallback: it reaches GitHub's two hosts as a header and
/// nothing else, and is in nothing that is written or reported.
#[test]
fn a_token_from_the_environment_fallback_leaks_nowhere_either() {
    use crate::publish::token::{Origin, StoredToken};
    const ENV_TOKEN: &str = "ghp_ENVFALLBACKTOKEN0123456789";
    let source = StoredToken::new(None, Some(ENV_TOKEN.into()));
    assert_eq!(source.resolve().unwrap().1, Origin::Environment);
    let f = fx();
    let t = FakeTransport::new();
    script_clean(&f, &t);
    // one answer echoes the token, one upload answer is lost
    let all = assets(&f);
    let provider = GithubProvider::new(&t, &source, "kiln-test/0");
    let mut events = Vec::new();
    let mut sink = |p: &Progress| events.push(p.clone());
    let o = Engine { provider: &provider, events: &mut sink, fault: &no_faults }.apply(&f.loaded).unwrap();
    assert_eq!(o.state, OutcomeState::DraftReady);
    let log = t.requests();
    assert_eq!(log.len(), 3 + 2 * all.len());
    for r in &log {
        assert!(r.host == API_HOST || r.host == UPLOAD_HOST);
        assert_eq!(r.header("authorization"), Some(format!("Bearer {ENV_TOKEN}").as_str()));
        assert!(!r.url.contains(ENV_TOKEN) && !String::from_utf8_lossy(&r.body).contains(ENV_TOKEN));
        assert!(r.headers.iter().filter(|(_, v)| v.contains(ENV_TOKEN)).count() == 1);
    }
    let mut text = serde_json::to_string(&events).unwrap() + &serde_json::to_string(&o).unwrap();
    for e in std::fs::read_dir(&f.loaded.dir).unwrap().flatten() {
        if e.file_name() != txdir::ARTIFACT {
            text += &String::from_utf8_lossy(&std::fs::read(e.path()).unwrap());
        }
    }
    assert!(!text.contains(ENV_TOKEN) && !text.contains("ghp_") && !text.contains(crate::publish::token::ENV));
    println!("PASS publish.no-credential-leak (environment fallback)");
}

#[test]
fn retry_after_cannot_echo_a_credential_into_the_journal_or_events() {
    use crate::publish::token::{SecretStore, StoredToken};
    struct Store;
    impl SecretStore for Store {
        fn get(&self) -> Result<Option<String>> { Ok(Some(TOKEN.into())) }
        fn set(&self, _: &str) -> Result<()> { unreachable!() }
        fn clear(&self) -> Result<()> { unreachable!() }
    }
    for source in [StoredToken::new(Some(Box::new(Store)), None), StoredToken::new(None, Some(TOKEN.into()))] {
        for header in [TOKEN, "https://example.invalid/?sig=SIGNEDSECRET", "60\nterminal output"] {
            let f = fx();
            let t = FakeTransport::new();
            t.reply(Method::Get, TAG_REF, Reply::status(404));
    t.reply(Method::Get, LIST, Reply::json(200, "[]"));
            t.reply(Method::Post, CREATE, Reply::json(403, "{}").with_header("Retry-After", header));
            let provider = GithubProvider::new(&t, &source, "kiln-test/0");
            let mut events = Vec::new();
            let mut sink = |p: &Progress| events.push(p.clone());
            let o = Engine { provider: &provider, events: &mut sink, fault: &no_faults }.apply(&f.loaded).unwrap();
            assert_eq!(o.state, OutcomeState::Refused);
            let text = serde_json::to_string(&events).unwrap() + &serde_json::to_string(&o).unwrap()
                + &std::fs::read_to_string(f.loaded.dir.join(txdir::JOURNAL)).unwrap();
            for secret in [TOKEN, "SIGNEDSECRET", "terminal output"] {
                assert!(!text.contains(secret), "provider header escaped into durable output");
            }
            check_log(&t);
        }
    }
}

#[test]
fn an_unreadable_upload_answer_is_reconciled_before_any_retry() {
    for reply in [Reply::bytes(201, &vec![b'x'; crate::net::DEFAULT_MAX_RESPONSE as usize + 1]),
        Reply::redirect(307, "https://uploads.github.com/elsewhere")] {
        let f = fx();
        let t = FakeTransport::new();
        t.reply(Method::Get, TAG_REF, Reply::status(404));
    t.reply(Method::Get, LIST, Reply::json(200, "[]"));
        t.reply(Method::Post, CREATE, Reply::json(201, &release_json(&f, 501, true, None)));
        let all = assets(&f);
        t.reply(Method::Get, RELEASE, Reply::json(200, &release_json(&f, 501, true, None)));
        t.reply(Method::Post, &upload_url(&all[0].0), reply);
        t.reply(Method::Get, ASSETS, Reply::json(200, &format!("[{}]", asset_json(900, &all[0].0, &all[0].1, true))));
        for (i, (name, file)) in all.iter().enumerate().skip(1) {
            t.reply(Method::Get, RELEASE, Reply::json(200, &release_json(&f, 501, true, None)));
            t.reply(Method::Post, &upload_url(name), Reply::json(201, &asset_json(900 + i as u64, name, file, true)));
        }
        assert_eq!(apply(&f, &t).state, OutcomeState::DraftReady);
        assert_eq!(check_log(&t).iter().filter(|r| r.url == upload_url(&all[0].0)).count(), 1);
        assert!(t.unused().is_empty());
    }
}

#[test]
fn a_refused_upload_is_looked_up_before_resume_retries_it() {
    for status in [403, 409, 422] {
        let f = fx();
        let t = FakeTransport::new();
        t.reply(Method::Get, TAG_REF, Reply::status(404));
    t.reply(Method::Get, LIST, Reply::json(200, "[]"));
        t.reply(Method::Post, CREATE, Reply::json(201, &release_json(&f, 501, true, None)));
        let all = assets(&f);
        t.reply(Method::Get, RELEASE, Reply::json(200, &release_json(&f, 501, true, None)));
        t.reply(Method::Post, &upload_url(&all[0].0), Reply::status(status));
        assert_eq!(apply(&f, &t).state, OutcomeState::Partial);
        t.reply(Method::Get, ASSETS, Reply::json(200, &format!("[{}]", asset_json(900, &all[0].0, &all[0].1, true))));
        for (i, (name, file)) in all.iter().enumerate().skip(1) {
            t.reply(Method::Get, RELEASE, Reply::json(200, &release_json(&f, 501, true, None)));
            t.reply(Method::Post, &upload_url(name), Reply::json(201, &asset_json(900 + i as u64, name, file, true)));
        }
        assert_eq!(apply(&f, &t).state, OutcomeState::DraftReady);
        assert_eq!(check_log(&t).iter().filter(|r| r.url == upload_url(&all[0].0)).count(), 1);
    }
}

#[test]
fn an_in_place_change_during_upload_cannot_be_confirmed() {
    struct Changing<'a> { fake: &'a FakeTransport, path: PathBuf }
    impl Transport for Changing<'_> {
        fn exchange(&self, req: crate::net::WireRequest) -> Result<Response> {
            if req.method == Method::Get { return self.fake.exchange(req); }
            let before = std::fs::read(&self.path).unwrap();
            let mut changed = before.clone();
            changed[0] ^= 1;
            let mut perm = std::fs::metadata(&self.path).unwrap().permissions();
            #[allow(clippy::permissions_set_readonly_false)]
            perm.set_readonly(false);
            std::fs::set_permissions(&self.path, perm).unwrap();
            std::fs::write(&self.path, changed).unwrap();
            let result = self.fake.exchange(req);
            // Even restoring the file before the next preflight must not hide what was streamed.
            std::fs::write(&self.path, before).unwrap();
            result
        }
    }
    let f = fx();
    let t = FakeTransport::new();
    let all = assets(&f);
    t.reply(Method::Get, RELEASE, Reply::json(200, &release_json(&f, 501, true, None)));
    t.reply(Method::Post, &upload_url(&all[0].0), Reply::json(201, &asset_json(900, &all[0].0, &all[0].1, true)));
    let transport = Changing { fake: &t, path: f.loaded.dir.join(txdir::ARTIFACT) };
    let provider = GithubProvider::new(&transport, &Tok, "kiln-test/0");
    let known = std::collections::BTreeMap::from([("draft".into(), RemoteIds::from([("release".into(), "501".into())]))]);
    let ctx = StepCtx { plan: &f.loaded.plan, op: &f.loaded.plan.operations[1], dir: &f.loaded.dir, known: &known, attempt: "test" };
    assert!(!matches!(provider.execute(&ctx), StepResult::Confirmed(_)), "changed bytes were accepted as the frozen artifact");
    check_log(&t);
}

#[test]
fn an_existing_tag_at_another_commit_blocks_creation_and_publication() {
    let wrong = serde_json::json!({"object": {"type": "commit", "sha": "f".repeat(40)}}).to_string();
    let f = fx();
    let t = FakeTransport::new();
    t.reply(Method::Get, LIST, Reply::json(200, "[]"));
    t.reply(Method::Get, TAG_REF, Reply::json(200, &wrong));
    let o = apply(&f, &t);
    assert!(!o.ok);
    assert!(t.requests().iter().all(|r| r.method == Method::Get), "created a draft against a different commit");
    check_log(&t);

    let f = fx();
    let t = FakeTransport::new();
    script_clean(&f, &t);
    assert!(apply(&f, &t).ok);
    t.reply(Method::Get, RELEASE, Reply::json(200, &release_json(&f, 501, true, None)));
    t.reply(Method::Get, TAG_REF, Reply::json(200, &wrong));
    let o = publish(&f, &t);
    assert!(!o.ok);
    assert!(t.requests().iter().all(|r| r.method != Method::Patch), "published a tag at a different commit");
    check_log(&t);
}

#[test]
fn an_annotated_tag_is_peeled_to_the_pinned_commit() {
    let f = fx();
    let t = FakeTransport::new();
    let sha = "a".repeat(40);
    t.reply(Method::Get, LIST, Reply::json(200, "[]"));
    t.reply(Method::Get, TAG_REF, Reply::json(200, &serde_json::json!({"object": {"type": "tag", "sha": sha}}).to_string()));
    t.reply(Method::Get, &format!("https://api.github.com/repos/example/good-mod/git/tags/{sha}"),
        Reply::json(200, &serde_json::json!({"object": {"type": "commit", "sha": COMMIT}}).to_string()));
    t.reply(Method::Post, CREATE, Reply::json(201, &release_json(&f, 501, true, None)));
    let provider = GithubProvider::new(&t, &Tok, "kiln-test/0");
    let known = std::collections::BTreeMap::new();
    let ctx = StepCtx { plan: &f.loaded.plan, op: &f.loaded.plan.operations[0], dir: &f.loaded.dir, known: &known, attempt: "test" };
    assert!(matches!(provider.execute(&ctx), StepResult::Confirmed(_)));
    check_log(&t);
    assert!(t.unused().is_empty());
}

#[test]
fn resuming_an_upload_never_adds_assets_to_a_public_or_foreign_release() {
    for (draft, marker) in [(false, None), (true, Some("foreign marker"))] {
        let f = fx();
        let t = FakeTransport::new();
        t.reply(Method::Get, RELEASE, Reply::json(200, &release_json(&f, 501, draft, marker)));
        let provider = GithubProvider::new(&t, &Tok, "kiln-test/0");
        let known = std::collections::BTreeMap::from([("draft".into(), RemoteIds::from([("release".into(), "501".into())]))]);
        let ctx = StepCtx { plan: &f.loaded.plan, op: &f.loaded.plan.operations[1], dir: &f.loaded.dir, known: &known, attempt: "test" };
        assert!(matches!(provider.execute(&ctx), StepResult::Refused(_)));
        assert!(check_log(&t).iter().all(|r| r.method == Method::Get), "an upload escaped onto a non-draft release");
        assert!(t.unused().is_empty());
    }
}

#[test]
fn an_upload_response_without_an_asset_id_is_not_confirmed() {
    let f = fx();
    let t = FakeTransport::new();
    t.reply(Method::Get, RELEASE, Reply::json(200, &release_json(&f, 501, true, None)));
    let all = assets(&f);
    let mut response: Value = serde_json::from_str(&asset_json(900, &all[0].0, &all[0].1, false)).unwrap();
    response.as_object_mut().unwrap().remove("id");
    t.reply(Method::Post, &upload_url(&all[0].0), Reply::json(201, &response.to_string()));
    let provider = GithubProvider::new(&t, &Tok, "kiln-test/0");
    let known = std::collections::BTreeMap::from([("draft".into(), RemoteIds::from([("release".into(), "501".into())]))]);
    let ctx = StepCtx { plan: &f.loaded.plan, op: &f.loaded.plan.operations[1], dir: &f.loaded.dir, known: &known, attempt: "test" };
    assert!(matches!(provider.execute(&ctx), StepResult::Unknown(_)));
    check_log(&t);
}

// ---------------------------------------------------------------- an asset GitHub lists without a digest

/// The frozen zip's bytes: what the draft asset holds when it is ours.
fn zip_bytes(f: &Fx) -> Vec<u8> {
    std::fs::read(f.loaded.dir.join(txdir::ARTIFACT)).unwrap()
}

/// An apply up to the zip's upload, whose answer is lost; the asset list then shows `listed`.
fn script_lost_upload(f: &Fx, t: &FakeTransport, listed: &str) {
    let all = assets(f);
    t.reply(Method::Get, TAG_REF, Reply::status(404));
    t.reply(Method::Get, LIST, Reply::json(200, "[]"));
    t.reply(Method::Post, CREATE, Reply::json(201, &release_json(f, 501, true, None)));
    t.reply(Method::Get, RELEASE, Reply::json(200, &release_json(f, 501, true, None)));
    t.reply(Method::Post, &upload_url(&all[0].0), Reply::Lost);
    t.reply(Method::Get, ASSETS, Reply::json(200, &format!("[{listed}]")));
}

/// The zip listed as uploaded, of the planned size, without a digest.
fn listed_without_digest(f: &Fx) -> String {
    let all = assets(f);
    asset_json(900, &all[0].0, &all[0].1, false)
}

fn script_remaining_uploads(f: &Fx, t: &FakeTransport) {
    for (i, (name, file)) in assets(f).iter().enumerate().skip(1) {
        t.reply(Method::Get, RELEASE, Reply::json(200, &release_json(f, 501, true, None)));
        t.reply(Method::Post, &upload_url(name), Reply::json(201, &asset_json(900 + i as u64, name, file, true)));
    }
}

/// Everything the transaction wrote to its folder (the frozen zip aside) and reported, as one text.
fn durable_text(f: &Fx, o: &crate::publish::engine::PublishOutcome, events: &[Progress]) -> String {
    let mut text = serde_json::to_string(events).unwrap() + &serde_json::to_string(o).unwrap();
    for e in std::fs::read_dir(&f.loaded.dir).unwrap().flatten() {
        if e.file_name() != txdir::ARTIFACT {
            text += &String::from_utf8_lossy(&std::fs::read(e.path()).unwrap());
        }
    }
    text
}

#[test]
fn a_lost_upload_answer_without_a_digest_is_adopted_by_hashing_the_download() {
    let objects = "https://objects.githubusercontent.com/github-production-release-asset-2e65be/900?X-Amz-Signature=SIGNEDSECRET";
    // GitHub redirects to either storage host, or answers with the bytes itself
    for hop in [Some(STORAGE_900), Some(objects), None] {
        let f = fx();
        let t = FakeTransport::new();
        script_lost_upload(&f, &t, &listed_without_digest(&f));
        match hop {
            Some(storage) => {
                t.reply(Method::Get, ASSET_900, Reply::redirect(302, storage));
                t.reply(Method::Get, storage, Reply::bytes(200, &zip_bytes(&f)));
            }
            None => {
                t.reply(Method::Get, ASSET_900, Reply::bytes(200, &zip_bytes(&f)));
            }
        }
        script_remaining_uploads(&f, &t);
        let provider = GithubProvider::new(&t, &Tok, "kiln-test/0");
        let mut events = Vec::new();
        let mut sink = |p: &Progress| events.push(p.clone());
        let o = Engine { provider: &provider, events: &mut sink, fault: &no_faults }.apply(&f.loaded).unwrap();
        assert_eq!(o.state, OutcomeState::DraftReady, "{hop:?}: {:?}", o.reason);
        assert_eq!(o.remote_ids["asset:good-mod-1.0.0.zip"]["asset"], "900");
        let log = check_log(&t);
        assert!(t.unused().is_empty(), "{hop:?}: {:?}", t.unused());
        assert_eq!(log.iter().filter(|r| r.url == upload_url(&assets(&f)[0].0)).count(), 1, "{hop:?}: uploaded again");
        // the download asks the API for the bytes, with the token; the redirect hop goes to GitHub's storage without it
        let dl: Vec<&Recorded> = log.iter().filter(|r| r.url == ASSET_900).collect();
        assert_eq!(dl.len(), 1);
        assert_eq!(dl[0].header("authorization"), Some(format!("Bearer {TOKEN}").as_str()));
        assert_eq!(dl[0].header("accept"), Some("application/octet-stream"));
        if let Some(storage) = hop {
            let second = log.iter().find(|r| r.url == storage).unwrap();
            assert!(ASSET_HOSTS.contains(&second.host.as_str()), "{}", second.host);
            assert_eq!(second.header("authorization"), None, "the redirect hop carried the token");
            assert_eq!(second.header("accept"), Some("application/octet-stream"));
        }
        // the signed storage address and the token are in nothing written or reported
        let text = durable_text(&f, &o, &events);
        assert!(!text.contains("SIGNEDSECRET") && !text.contains(TOKEN), "{hop:?}");
    }
    println!("PASS publish.github-fixtures (an asset without a digest is adopted by the SHA-256 of its download)");
}

#[test]
fn other_bytes_under_the_name_without_a_digest_are_a_conflict_and_never_uploaded_over() {
    let f = fx();
    let t = FakeTransport::new();
    script_lost_upload(&f, &t, &listed_without_digest(&f));
    let mut other = zip_bytes(&f);
    other[0] ^= 0xff;
    t.reply(Method::Get, ASSET_900, Reply::redirect(302, STORAGE_900));
    t.reply(Method::Get, STORAGE_900, Reply::bytes(200, &other));
    let o = apply(&f, &t);
    assert_eq!(o.state, OutcomeState::ManualReconcile);
    assert!(o.reason.as_deref().unwrap().contains("other content"), "{:?}", o.reason);
    assert_eq!(o.confirmed_steps, ["draft"]);
    let j: crate::publish::journal::Journal = crate::util::read_json(&f.loaded.dir.join(txdir::JOURNAL)).unwrap().unwrap();
    assert_eq!(j.steps.iter().find(|s| s.id == "asset:good-mod-1.0.0.zip").unwrap().state, StepState::ManualReconcile);
    // a resume neither uploads over it nor deletes it: it sends nothing at all for that step
    let sent = t.requests().len();
    assert_eq!(apply(&f, &t).state, OutcomeState::ManualReconcile);
    assert_eq!(t.requests().len(), sent, "a resume touched the conflicting asset");
    let log = check_log(&t);
    assert_eq!(log.iter().filter(|r| r.host == UPLOAD_HOST).count(), 1);
    assert!(t.unused().is_empty(), "{:?}", t.unused());
}

#[test]
fn a_download_that_fails_or_goes_elsewhere_leaves_the_asset_to_a_person() {
    let elsewhere = "https://github-cloud.s3.amazonaws.com/releases/900?X-Amz-Signature=SIGNEDSECRET";
    for what in ["lost", "refused", "other host", "storage refused", "too long", "short", "second redirect"] {
        let f = fx();
        let t = FakeTransport::new();
        script_lost_upload(&f, &t, &listed_without_digest(&f));
        let zip = zip_bytes(&f);
        match what {
            "lost" => {
                t.reply(Method::Get, ASSET_900, Reply::Lost);
            }
            "refused" => {
                t.reply(Method::Get, ASSET_900, Reply::json(403, r#"{"message": "Forbidden"}"#));
            }
            "other host" => {
                t.reply(Method::Get, ASSET_900, Reply::redirect(302, elsewhere));
                t.reply(Method::Get, elsewhere, Reply::bytes(200, &zip));
            }
            _ => {
                t.reply(Method::Get, ASSET_900, Reply::redirect(302, STORAGE_900));
                let answer = match what {
                    "storage refused" => Reply::bytes(403, b"<Error><Code>AuthenticationFailed</Code></Error>"),
                    "too long" => Reply::bytes(200, &[zip.as_slice(), &b"x"[..]].concat()),
                    "short" => Reply::bytes(200, &zip[..zip.len() - 1]),
                    _ => Reply::redirect(302, "https://objects.githubusercontent.com/again"),
                };
                t.reply(Method::Get, STORAGE_900, answer);
            }
        }
        let o = apply(&f, &t);
        assert_eq!(o.state, OutcomeState::ManualReconcile, "{what}");
        let why = o.reason.clone().unwrap();
        assert!(why.contains("compare it by hand"), "{what}: {why}");
        assert!(!why.contains("SIGNEDSECRET") && !why.contains(TOKEN), "{what}: {why}");
        let log = check_log(&t);
        assert_eq!(log.iter().filter(|r| r.host == UPLOAD_HOST).count(), 1, "{what}: uploaded again");
        assert!(log.iter().all(|r| r.host != "github-cloud.s3.amazonaws.com"), "{what}: a host outside the policy was asked");
        if what == "other host" {
            assert_eq!(t.unused(), [format!("GET {elsewhere}")]);
        } else {
            assert!(t.unused().is_empty(), "{what}: {:?}", t.unused());
        }
    }
}

#[test]
fn the_download_is_bounded_by_the_plan_and_by_the_artifact_policy() {
    // listed with another size than planned: a conflict, and nothing is downloaded
    let f = fx();
    let t = FakeTransport::new();
    let all = assets(&f);
    let bigger = plan::FrozenFile { bytes: all[0].1.bytes + 1, ..all[0].1.clone() };
    script_lost_upload(&f, &t, &asset_json(900, &all[0].0, &bigger, false));
    let o = apply(&f, &t);
    assert_eq!(o.state, OutcomeState::ManualReconcile);
    assert!(o.reason.as_deref().unwrap().contains("another size"), "{:?}", o.reason);
    assert!(check_log(&t).iter().all(|r| !r.url.contains("/releases/assets/")), "an asset of another size was downloaded");
    // one byte over the planned size fails ("too long" above); a planned size over the policy's limit is never asked
    // for (a valid plan cannot hold one; the provider does not rely on that)
    let t = FakeTransport::new();
    let provider = GithubProvider::new(&t, &Tok, "kiln-test/0");
    let huge = plan::FrozenFile { bytes: crate::publish::policy::MAX_ZIP_BYTES + 1, ..f.loaded.plan.files.artifact.clone() };
    match provider.hash_asset(&f.loaded.plan, "900", &huge) {
        Reconcile::Ambiguous(why) => assert!(why.contains("larger than a published zip"), "{why}"),
        other => panic!("{other:?}"),
    }
    // and an id that is not a number is not put into an address
    assert!(matches!(provider.hash_asset(&f.loaded.plan, "900/../../x", &f.loaded.plan.files.artifact), Reconcile::Ambiguous(_)));
    assert!(t.requests().is_empty());
}

#[test]
fn resolve_step_adopts_an_asset_without_a_digest_through_its_download() {
    let f = fx();
    let t = FakeTransport::new();
    script_lost_upload(&f, &t, &listed_without_digest(&f));
    t.reply(Method::Get, ASSET_900, Reply::Lost); // the reconcile's own download fails: a person has to look
    let o = apply(&f, &t);
    assert_eq!(o.state, OutcomeState::ManualReconcile);
    let step = "asset:good-mod-1.0.0.zip";
    assert!(o.manual_steps.iter().any(|m| m.contains(&format!("--resolve-step {step}"))), "{:?}", o.manual_steps);
    let downloads = |t: &FakeTransport| t.requests().iter().filter(|r| r.url == ASSET_900).count();
    assert_eq!(downloads(&t), 1);

    let provider = GithubProvider::new(&t, &Tok, "kiln-test/0");
    let mut sink = |_: &Progress| {};
    let mut engine = Engine { provider: &provider, events: &mut sink, fault: &no_faults };
    let listed = format!("[{}]", listed_without_digest(&f));
    // another id than the listed asset's: refused before anything is downloaded
    t.reply(Method::Get, ASSETS, Reply::json(200, &listed));
    assert_eq!(engine.resolve_step(&f.loaded, step, "901").unwrap_err().code, crate::ErrorCode::Refused);
    assert_eq!(downloads(&t), 1);
    // the right id with other bytes behind it: refused, the step still waits
    let mut other = zip_bytes(&f);
    other[1] ^= 0xff;
    t.reply(Method::Get, ASSETS, Reply::json(200, &listed));
    t.reply(Method::Get, ASSET_900, Reply::redirect(302, STORAGE_900));
    t.reply(Method::Get, STORAGE_900, Reply::bytes(200, &other));
    let e = engine.resolve_step(&f.loaded, step, "900").unwrap_err();
    assert!(e.message.contains("other content"), "{}", e.message);
    // the right id with the planned bytes: downloaded, hashed, adopted
    t.reply(Method::Get, ASSETS, Reply::json(200, &listed));
    t.reply(Method::Get, ASSET_900, Reply::redirect(302, STORAGE_900));
    t.reply(Method::Get, STORAGE_900, Reply::bytes(200, &zip_bytes(&f)));
    let o = engine.resolve_step(&f.loaded, step, "900").unwrap();
    assert_eq!(o.remote_ids[step]["asset"], "900");
    assert_eq!(downloads(&t), 3);
    // the resume goes on with the other two assets; the zip is never uploaded a second time
    script_remaining_uploads(&f, &t);
    assert_eq!(engine.apply(&f.loaded).unwrap().state, OutcomeState::DraftReady);
    let log = check_log(&t);
    assert_eq!(log.iter().filter(|r| r.url == upload_url(&assets(&f)[0].0)).count(), 1);
    assert!(t.unused().is_empty(), "{:?}", t.unused());
    println!("PASS publish.github-fixtures (--resolve-step adopts an asset without a digest by the SHA-256 of its download)");
}
