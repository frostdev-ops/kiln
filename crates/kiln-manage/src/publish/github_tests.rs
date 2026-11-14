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
    t.reply(Method::Get, LIST, Reply::json(200, "[]"));
    t.reply(Method::Post, CREATE, Reply::json(201, &release_json(f, 501, true, None)));
    for (i, (name, file)) in assets(f).iter().enumerate() {
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
        assert!(r.host == API_HOST || r.host == UPLOAD_HOST, "a request went to {}", r.host);
        assert_eq!(r.header("authorization"), Some(format!("Bearer {TOKEN}").as_str()));
        assert!(matches!(r.method, Method::Get | Method::Post | Method::Patch), "{:?}", r.method);
        assert!(!r.url.contains("/git/") && !r.url.contains("/tags"), "a tag was touched: {}", r.url);
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
    t.reply(Method::Get, LIST, Reply::json(200, "[]"));
    t.reply(Method::Post, CREATE, Reply::Lost);
    t.reply(Method::Get, LIST, Reply::json(200, &format!("[{}]", release_json(&f, 501, true, None))));
    for (i, (name, file)) in assets(&f).iter().enumerate() {
        t.reply(Method::Post, &upload_url(name), Reply::json(201, &asset_json(900 + i as u64, name, file, true)));
    }
    let o = apply(&f, &t);
    assert_eq!(o.state, OutcomeState::DraftReady);
    assert_eq!(check_log(&t).iter().filter(|r| r.url == CREATE).count(), 1, "the draft was created twice");

    // the answer was lost and nothing arrived: one more attempt, then the draft exists once
    let f = fx();
    let t = FakeTransport::new();
    t.reply(Method::Get, LIST, Reply::json(200, "[]")).reply(Method::Post, CREATE, Reply::Lost);
    t.reply(Method::Get, LIST, Reply::json(200, "[]")); // reconcile: not there
    t.reply(Method::Get, LIST, Reply::json(200, "[]")); // the second attempt's own look
    t.reply(Method::Post, CREATE, Reply::json(201, &release_json(&f, 501, true, None)));
    for (i, (name, file)) in assets(&f).iter().enumerate() {
        t.reply(Method::Post, &upload_url(name), Reply::json(201, &asset_json(900 + i as u64, name, file, true)));
    }
    assert_eq!(apply(&f, &t).state, OutcomeState::DraftReady);
    check_log(&t);

    // two drafts with our marker, or a list that cannot be read: a person has to look
    let f = fx();
    let t = FakeTransport::new();
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
        t.reply(Method::Get, LIST, Reply::json(200, "[]"));
        t.reply(Method::Post, CREATE, Reply::json(201, &release_json(f, 501, true, None)));
    };
    // lost answer, the asset is there with our digest: adopted, not uploaded again
    let f = fx();
    let t = FakeTransport::new();
    script_until_first_upload(&f, &t);
    let all = assets(&f);
    t.reply(Method::Post, &upload_url(&all[0].0), Reply::Lost);
    t.reply(Method::Get, ASSETS, Reply::json(200, &format!("[{}]", asset_json(900, &all[0].0, &all[0].1, true))));
    for (i, (name, file)) in all.iter().enumerate().skip(1) {
        t.reply(Method::Post, &upload_url(name), Reply::json(201, &asset_json(900 + i as u64, name, file, true)));
    }
    assert_eq!(apply(&f, &t).state, OutcomeState::DraftReady);
    assert_eq!(check_log(&t).iter().filter(|r| r.url == upload_url(&all[0].0)).count(), 1);

    // a partial upload holds the name; same name with another size; same size with another digest; no digest
    let partial = serde_json::json!({"id": 900, "name": all[0].0, "size": 0, "state": "starter"}).to_string();
    for what in ["partial", "size", "digest", "no digest"] {
        let f = fx();
        let t = FakeTransport::new();
        script_until_first_upload(&f, &t);
        let name = &assets(&f)[0].0;
        t.reply(Method::Post, &upload_url(name), Reply::Lost);
        let listed = match what {
            "size" => asset_json(900, name, &plan::FrozenFile { bytes: f.loaded.plan.files.artifact.bytes + 1, ..f.loaded.plan.files.artifact.clone() }, true),
            "digest" => asset_json(900, name, &plan::FrozenFile { sha256: "f".repeat(64), ..f.loaded.plan.files.artifact.clone() }, true),
            "no digest" => asset_json(900, name, &f.loaded.plan.files.artifact, false),
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
    t.reply(Method::Get, LIST, Reply::json(200, "[]"));
    t.reply(Method::Post, CREATE, Reply::json(201, &release_json(&f, 501, true, None)));
    let all = assets(&f);
    t.reply(Method::Post, &upload_url(&all[0].0), Reply::json(403, "{}").with_header("Retry-After", "5"));
    let o = apply(&f, &t);
    assert_eq!((o.state, o.confirmed_steps.clone()), (OutcomeState::Partial, vec!["draft".to_string()]));
    for (i, (name, file)) in all.iter().enumerate() {
        t.reply(Method::Post, &upload_url(name), Reply::json(201, &asset_json(900 + i as u64, name, file, true)));
    }
    assert_eq!(apply(&f, &t).state, OutcomeState::DraftReady);
    assert_eq!(check_log(&t).iter().filter(|r| r.url == CREATE).count(), 1);
    // a server error is not a refusal: the outcome is unknown and is looked up
    let f = fx();
    let t = FakeTransport::new();
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
    t.reply(Method::Patch, RELEASE, Reply::Lost);
    t.reply(Method::Get, RELEASE, Reply::json(200, &release_json(&f, 501, false, None)));
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
    t.reply(Method::Get, LIST, Reply::json(200, "[]"));
    t.reply(Method::Post, CREATE, Reply::json(201, &release_json(&f, 501, true, None)));
    let all = assets(&f);
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
