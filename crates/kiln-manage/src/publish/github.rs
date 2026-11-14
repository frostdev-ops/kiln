//! The GitHub provider of `kiln publish`: a draft release at the planned tag and commit, the three assets, and (as
//! its own step) making the draft public. Written from GitHub's public REST documentation ("Releases", "Release
//! assets", API version 2022-11-28); no request of it has been sent to GitHub by its authors. Until the owner-run
//! check `publish.github-real` has run, it is proven against recorded-style answers only.
//!
//! What it never does: delete anything, replace an asset, move or create a tag by hand, publish as part of an apply,
//! send the token anywhere but `api.github.com` and `uploads.github.com`, follow a redirect with the token.
//!
//! Finding our own release again. A draft release has no tag in the repository yet, so it cannot be looked up by
//! tag, and GitHub lets several drafts carry the same tag name. The provider therefore lists the repository's
//! releases (drafts are listed for a token that may write) and recognises its own by the marker at the end of the
//! body (`<!-- kiln-publish:v1 tx=… plan=… -->`), the tag name and the target commit. Any other release or draft
//! with the planned tag is a conflict: the step is refused before anything is created.

use super::plan::{self, PublishPlan};
use super::provider::{PublishProvider, Reconcile, RemoteIds, StepCtx, StepResult};
use super::txdir;
use crate::net::{Body, Client, Method, Policy, Request, Response, Transport};
use crate::Result;
use serde_json::Value;

pub const API_HOST: &str = "api.github.com";
pub const UPLOAD_HOST: &str = "uploads.github.com";
const API_VERSION: &str = "2022-11-28";
/// Releases are listed 100 a page, at most this many pages: beyond that "not found" cannot be claimed.
const MAX_PAGES: u32 = 5;
const LIST_MAX_RESPONSE: u64 = 8 * 1024 * 1024;

/// Where the token comes from (the OS credential store in the CLI, a constant in tests). Never the plan, the config
/// or the journal.
pub trait TokenSource {
    fn token(&self) -> Result<String>;
}

pub fn policy() -> Policy {
    Policy::new("GitHub").credential_host(API_HOST).credential_host(UPLOAD_HOST)
}

pub struct GithubProvider<'a> {
    client: Client<'a>,
    token: &'a dyn TokenSource,
}

enum Call {
    Ok(Response),
    /// A definite no: nothing was changed.
    Refused(String),
    /// No usable answer: the request may have arrived.
    Unknown(String),
}

fn refusal(r: &Response, what: &str) -> Option<String> {
    let rate = r.header("x-ratelimit-remaining") == Some("0") || r.header("retry-after").is_some();
    // A provider header is untrusted and may echo a credential or a signed URL. Persist only a parsed delay.
    let wait = r.header("retry-after").and_then(|s| s.parse::<u64>().ok()).map(|s| format!(" (retry after {s} s)")).unwrap_or_default();
    Some(match r.status {
        401 => format!("GitHub did not accept the token (401) while {what}"),
        403 | 429 if rate => format!("GitHub's rate limit stopped {what} ({}){wait}", r.status),
        403 => format!("the token may not do this (403) while {what}: it needs write access to the repository's contents"),
        404 => format!("GitHub does not show this repository or release to the token (404) while {what}"),
        409 | 422 => format!("GitHub refused {what} ({}): what it should create exists already or is not valid", r.status),
        429 => format!("GitHub's rate limit stopped {what} (429){wait}"),
        s if (400..500).contains(&s) => format!("GitHub refused {what} ({s})"),
        _ => return None,
    })
}

impl<'a> GithubProvider<'a> {
    pub fn new(transport: &'a dyn Transport, token: &'a dyn TokenSource, user_agent: &str) -> GithubProvider<'a> {
        GithubProvider { client: Client::new(policy(), transport, user_agent), token }
    }

    fn send(&self, req: Request, what: &str) -> Call {
        let token = match self.token.token() {
            Ok(t) if !t.is_empty() => t,
            _ => return Call::Refused("no GitHub token is stored for kiln publish".into()),
        };
        let req = req.header("Authorization", &format!("Bearer {token}")).header("Accept", "application/vnd.github+json").header("X-GitHub-Api-Version", API_VERSION);
        match self.client.send(req) {
            // A bounded-body failure or a refused redirect can occur after a mutation reached GitHub, just like
            // a network failure. A transport error never proves that a mutation did not happen.
            Err(_) => Call::Unknown(format!("no usable answer while {what}")),
            Ok(r) if (200..300).contains(&r.status) => Call::Ok(r),
            Ok(r) => match refusal(&r, what) {
                Some(why) => Call::Refused(why),
                None => Call::Unknown(format!("GitHub answered {} while {what}", r.status)),
            },
        }
    }

    fn repo_url(&self, plan: &PublishPlan, tail: &str) -> String {
        format!("https://{API_HOST}/repos/{}{tail}", plan.destination.repository)
    }

    /// Every release and draft of the repository, or why the list is not complete.
    fn list(&self, plan: &PublishPlan) -> std::result::Result<Vec<Value>, String> {
        let mut all = Vec::new();
        for page in 1..=MAX_PAGES {
            let url = self.repo_url(plan, &format!("/releases?per_page=100&page={page}"));
            let r = match self.send(Request::get(url).max_response(LIST_MAX_RESPONSE), "listing the releases") {
                Call::Ok(r) => r,
                Call::Refused(why) | Call::Unknown(why) => return Err(why),
            };
            let Ok(Value::Array(items)) = serde_json::from_slice::<Value>(&r.body) else { return Err("GitHub's release list could not be read".into()) };
            let n = items.len();
            all.extend(items);
            if n < 100 {
                return Ok(all);
            }
        }
        Err(format!("the repository has more than {} releases; the list was not read to its end", MAX_PAGES * 100))
    }

    fn marker(plan: &PublishPlan) -> String {
        plan::body_marker(&plan.tx_id, &plan.digest)
    }

    /// Whether a release object is this transaction's: marker, tag name and target commit.
    fn is_ours(plan: &PublishPlan, rel: &Value) -> bool {
        rel["body"].as_str().is_some_and(|b| b.trim_end().ends_with(&Self::marker(plan)))
            && rel["tag_name"].as_str() == Some(plan.destination.tag.as_str())
            && rel["target_commitish"].as_str() == Some(plan.target_commit.as_str())
    }

    fn id_of(v: &Value) -> Option<String> {
        v["id"].as_u64().map(|n| n.to_string())
    }

    /// What exists for the planned tag: ours (one), nothing, or something in the way.
    fn find_release(&self, plan: &PublishPlan) -> Reconcile {
        let list = match self.list(plan) {
            Ok(l) => l,
            Err(why) => return Reconcile::Ambiguous(why),
        };
        let same_tag: Vec<&Value> = list.iter().filter(|r| r["tag_name"].as_str() == Some(plan.destination.tag.as_str())).collect();
        let ours: Vec<&&Value> = same_tag.iter().filter(|r| Self::is_ours(plan, r)).collect();
        match (same_tag.len(), ours.len()) {
            (0, _) => Reconcile::NotThere,
            (1, 1) => match Self::id_of(ours[0]) {
                Some(id) => Reconcile::Adopt(RemoteIds::from([("release".into(), id)])),
                None => Reconcile::Ambiguous("GitHub's answer had no release id".into()),
            },
            (_, 0) => Reconcile::Conflict(format!("a release or draft for tag {} exists that this transaction did not make", plan.destination.tag)),
            _ => Reconcile::Ambiguous(format!("several releases or drafts carry tag {}", plan.destination.tag)),
        }
    }

    fn release_id(ctx: &StepCtx) -> Option<String> {
        ctx.known.get("draft").and_then(|ids| ids.get("release")).cloned()
    }

    fn create_draft(&self, ctx: &StepCtx) -> StepResult {
        // Look first: an existing release or draft for the tag is never touched, and a second draft is never made.
        match self.find_release(ctx.plan) {
            Reconcile::NotThere => {}
            Reconcile::Adopt(_) => return StepResult::Unknown("a draft of this transaction exists already".into()),
            Reconcile::Conflict(why) => return StepResult::Refused(why),
            Reconcile::Ambiguous(why) => return StepResult::Refused(why),
        }
        if let Err(why) = self.check_tag(ctx.plan) { return StepResult::Refused(why); }
        let notes = match std::fs::read_to_string(ctx.dir.join(txdir::NOTES)) {
            Ok(n) => n,
            Err(_) => return StepResult::Refused("the frozen notes cannot be read".into()),
        };
        let body = format!("{}\n\n{}", notes.trim_end(), Self::marker(ctx.plan));
        let payload = serde_json::json!({
            "tag_name": ctx.plan.destination.tag,
            "target_commitish": ctx.plan.target_commit,
            "name": format!("{} {}", ctx.plan.mod_.id, ctx.plan.mod_.version),
            "body": body.trim_start(),
            "draft": true,
            "prerelease": ctx.plan.destination.prerelease,
        });
        match self.send(Request::new(Method::Post, self.repo_url(ctx.plan, "/releases")).json(&payload), "creating the draft release") {
            Call::Refused(why) => StepResult::Refused(why),
            Call::Unknown(why) => StepResult::Unknown(why),
            Call::Ok(r) => match serde_json::from_slice::<Value>(&r.body) {
                Ok(v) if v["draft"].as_bool() == Some(true) && Self::is_ours(ctx.plan, &v) && Self::id_of(&v).is_some() => {
                    StepResult::Confirmed(RemoteIds::from([("release".into(), Self::id_of(&v).unwrap_or_default())]))
                }
                // created, but not as asked (or unreadable): let the reconcile look at what is there
                _ => StepResult::Unknown("GitHub's answer to the draft was not what was asked for".into()),
            },
        }
    }

    fn planned_file<'p>(ctx: &'p StepCtx) -> Option<&'p plan::FrozenFile> {
        let f = &ctx.plan.files;
        let name = ctx.op.file.as_deref()?;
        [&f.artifact, &f.checksum, &f.release].into_iter().find(|x| x.path == name)
    }

    /// An asset object against the planned file: ours, something else, or not decidable.
    fn judge_asset(asset: &Value, file: &plan::FrozenFile) -> Reconcile {
        let Some(id) = Self::id_of(asset) else { return Reconcile::Ambiguous("GitHub's answer had no asset id".into()) };
        if asset["state"].as_str() != Some("uploaded") {
            return Reconcile::Conflict("an unfinished upload holds the asset's name; remove it at GitHub, then resume".into());
        }
        if asset["size"].as_u64() != Some(file.bytes) {
            return Reconcile::Conflict("an asset of that name with another size exists".into());
        }
        match asset["digest"].as_str() {
            Some(d) if d == format!("sha256:{}", file.sha256) => Reconcile::Adopt(RemoteIds::from([("asset".into(), id)])),
            Some(_) => Reconcile::Conflict("an asset of that name with other content exists".into()),
            None => Reconcile::Ambiguous("GitHub gave no digest for the asset; compare it by hand".into()),
        }
    }

    fn find_asset(&self, ctx: &StepCtx) -> Reconcile {
        let (Some(release), Some(name), Some(file)) = (Self::release_id(ctx), ctx.op.asset.as_deref(), Self::planned_file(ctx)) else {
            return Reconcile::Ambiguous("the draft release of this transaction is not known".into());
        };
        let url = self.repo_url(ctx.plan, &format!("/releases/{release}/assets?per_page=100"));
        let r = match self.send(Request::get(url).max_response(LIST_MAX_RESPONSE), "listing the release's assets") {
            Call::Ok(r) => r,
            Call::Refused(why) | Call::Unknown(why) => return Reconcile::Ambiguous(why),
        };
        let Ok(Value::Array(items)) = serde_json::from_slice::<Value>(&r.body) else { return Reconcile::Ambiguous("GitHub's asset list could not be read".into()) };
        if items.len() >= 100 {
            return Reconcile::Ambiguous("the release has 100 or more assets; the list was not read to its end".into());
        }
        let named: Vec<&Value> = items.iter().filter(|a| a["name"].as_str() == Some(name)).collect();
        match named.as_slice() {
            [] => Reconcile::NotThere,
            [one] => Self::judge_asset(one, file),
            _ => Reconcile::Ambiguous("several assets carry that name".into()),
        }
    }

    fn upload(&self, ctx: &StepCtx) -> StepResult {
        let (Some(release), Some(name), Some(file)) = (Self::release_id(ctx), ctx.op.asset.as_deref(), Self::planned_file(ctx)) else {
            return StepResult::Refused("the draft release of this transaction is not known".into());
        };
        // A draft can have been published or edited by hand since the previous step. Resuming must not make an
        // asset public as a side effect or write into a release whose identity no longer matches the plan.
        match self.get_release(ctx.plan, &release) {
            Ok(rel) if Self::is_ours(ctx.plan, &rel) && Self::id_of(&rel).as_deref() == Some(release.as_str())
                && rel["draft"].as_bool() == Some(true) => {}
            Ok(_) => return StepResult::Refused("the release is no longer this transaction's draft; no asset was uploaded".into()),
            Err(_) => return StepResult::Refused("the draft release could not be verified; no asset was uploaded".into()),
        }
        // The one opened, verified handle is what is streamed: the path is not opened again.
        let handle = match txdir::open_verified(ctx.dir, &file.path, &file.sha256, Some(file.bytes)) {
            Ok(h) => h,
            Err(e) => return StepResult::Refused(e.message),
        };
        let url = format!("https://{UPLOAD_HOST}/repos/{}/releases/{release}/assets?name={name}", ctx.plan.destination.repository);
        let (reader, verified) = txdir::UploadReader::new(handle, file.bytes, &file.sha256);
        let req = Request::new(Method::Post, url).header("Content-Type", "application/octet-stream").body(Body::Reader { reader: Box::new(reader), len: file.bytes });
        match self.send(req, "uploading an asset") {
            Call::Refused(why) => StepResult::Refused(why),
            Call::Unknown(why) => StepResult::Unknown(why),
            Call::Ok(_) if !verified.load(std::sync::atomic::Ordering::Acquire) => StepResult::Unknown("the frozen upload was not streamed and verified in full".into()),
            Call::Ok(r) => match serde_json::from_slice::<Value>(&r.body) {
                Ok(v) if v["name"].as_str() == Some(name) => match Self::judge_asset(&v, file) {
                    Reconcile::Adopt(ids) => StepResult::Confirmed(ids),
                    // No digest in the answer: size and name agree and these bytes were just streamed from the verified
                    // handle, so it is ours.
                    Reconcile::Ambiguous(_) if Self::id_of(&v).is_some() && v["size"].as_u64() == Some(file.bytes) && v["state"].as_str() == Some("uploaded") => {
                        StepResult::Confirmed(RemoteIds::from([("asset".into(), Self::id_of(&v).unwrap_or_default())]))
                    }
                    _ => StepResult::Unknown("GitHub's answer to the upload does not match what was sent".into()),
                },
                _ => StepResult::Unknown("GitHub's answer to the upload could not be read".into()),
            },
        }
    }

    fn get_release(&self, plan: &PublishPlan, id: &str) -> std::result::Result<Value, Reconcile> {
        if id.is_empty() || !id.bytes().all(|b| b.is_ascii_digit()) {
            return Err(Reconcile::Conflict("a release id is a number".into()));
        }
        match self.send(Request::get(self.repo_url(plan, &format!("/releases/{id}"))), "reading the release") {
            Call::Ok(r) => serde_json::from_slice::<Value>(&r.body).map_err(|_| Reconcile::Ambiguous("GitHub's answer could not be read".into())),
            Call::Refused(why) if why.contains("(404)") => Err(Reconcile::NotThere),
            Call::Refused(why) | Call::Unknown(why) => Err(Reconcile::Ambiguous(why)),
        }
    }

    /// GitHub ignores target_commitish when the tag already exists. Check the actual ref, peeling annotated tags,
    /// before creating or publishing a release. Only GETs: an existing tag is never moved.
    fn check_tag(&self, plan: &PublishPlan) -> std::result::Result<(), String> {
        let url = self.repo_url(plan, &format!("/git/ref/tags/{}", plan.destination.tag));
        let mut object = match self.send(Request::get(url), "checking the release tag") {
            Call::Ok(r) => serde_json::from_slice::<Value>(&r.body).map_err(|_| "GitHub's tag reference could not be read".to_string())?,
            Call::Refused(why) if why.contains("(404)") => return Ok(()), // GitHub will create the tag at the pinned commit.
            Call::Refused(why) | Call::Unknown(why) => return Err(why),
        };
        for _ in 0..8 {
            let target = &object["object"];
            let sha = target["sha"].as_str().filter(|s| super::config::is_commit(s)).ok_or("GitHub's tag target has no valid SHA")?;
            match target["type"].as_str() {
                Some("commit") if sha == plan.target_commit => return Ok(()),
                Some("commit") => return Err("the existing tag points at another commit; Kiln will not move it".into()),
                Some("tag") => {
                    object = match self.send(Request::get(self.repo_url(plan, &format!("/git/tags/{sha}"))), "reading an annotated tag") {
                        Call::Ok(r) => serde_json::from_slice::<Value>(&r.body).map_err(|_| "GitHub's annotated tag could not be read".to_string())?,
                        Call::Refused(why) | Call::Unknown(why) => return Err(why),
                    };
                }
                _ => return Err("the existing tag does not point at a commit".into()),
            }
        }
        Err("the existing tag has too many nested annotated tags".into())
    }

    /// Draft or public, for the publish step: only ever the release this transaction made.
    fn published_state(&self, ctx: &StepCtx) -> Reconcile {
        let Some(id) = Self::release_id(ctx) else { return Reconcile::Ambiguous("the draft release of this transaction is not known".into()) };
        let rel = match self.get_release(ctx.plan, &id) {
            Ok(v) => v,
            Err(Reconcile::NotThere) => return Reconcile::Conflict("the draft release is gone".into()),
            Err(other) => return other,
        };
        if !Self::is_ours(ctx.plan, &rel) || Self::id_of(&rel).as_deref() != Some(id.as_str()) {
            return Reconcile::Conflict("the release no longer carries this transaction's marker, tag or commit".into());
        }
        match rel["draft"].as_bool() {
            Some(false) => match self.check_tag(ctx.plan) {
                Ok(()) => Reconcile::Adopt(RemoteIds::from([("release".into(), id)])),
                Err(why) => Reconcile::Conflict(why),
            },
            Some(true) => Reconcile::NotThere,
            None => Reconcile::Ambiguous("GitHub's answer does not say whether the release is a draft".into()),
        }
    }

    fn publish(&self, ctx: &StepCtx) -> StepResult {
        // Only the release with our marker is ever published, and only from the draft state.
        match self.published_state(ctx) {
            Reconcile::NotThere => {}
            Reconcile::Adopt(ids) => return StepResult::Confirmed(ids),
            Reconcile::Conflict(why) | Reconcile::Ambiguous(why) => return StepResult::Refused(why),
        }
        if let Err(why) = self.check_tag(ctx.plan) { return StepResult::Refused(why); }
        let id = Self::release_id(ctx).unwrap_or_default();
        let req = Request::new(Method::Patch, self.repo_url(ctx.plan, &format!("/releases/{id}"))).json(&serde_json::json!({"draft": false}));
        match self.send(req, "publishing the draft") {
            Call::Refused(why) => StepResult::Refused(why),
            Call::Unknown(why) => StepResult::Unknown(why),
            Call::Ok(r) => match serde_json::from_slice::<Value>(&r.body) {
                Ok(v) if v["draft"].as_bool() == Some(false) && Self::id_of(&v).as_deref() == Some(id.as_str()) => StepResult::Confirmed(RemoteIds::from([("release".into(), id)])),
                _ => StepResult::Unknown("GitHub's answer to publishing could not be read".into()),
            },
        }
    }
}

impl PublishProvider for GithubProvider<'_> {
    fn name(&self) -> &str {
        "github"
    }

    fn execute(&self, ctx: &StepCtx) -> StepResult {
        match ctx.op.kind.as_str() {
            "create-draft" => self.create_draft(ctx),
            "upload-asset" => self.upload(ctx),
            "publish-draft" => self.publish(ctx),
            other => StepResult::Refused(format!("the GitHub provider has no step of kind {other}")),
        }
    }

    fn reconcile(&self, ctx: &StepCtx) -> Reconcile {
        match ctx.op.kind.as_str() {
            "create-draft" => self.find_release(ctx.plan),
            "upload-asset" => self.find_asset(ctx),
            "publish-draft" => self.published_state(ctx),
            other => Reconcile::Ambiguous(format!("the GitHub provider has no step of kind {other}")),
        }
    }

    fn verify(&self, ctx: &StepCtx, remote_id: &str) -> Reconcile {
        match ctx.op.kind.as_str() {
            "create-draft" => match self.get_release(ctx.plan, remote_id) {
                Ok(rel) if Self::is_ours(ctx.plan, &rel) && rel["draft"].as_bool() == Some(true) => Reconcile::Adopt(RemoteIds::from([("release".into(), remote_id.to_string())])),
                Ok(_) => Reconcile::Conflict("that release does not carry this transaction's marker, tag and commit as a draft".into()),
                Err(r) => r,
            },
            "upload-asset" => {
                // the asset has to be one of our release's assets, by name, size and digest
                match self.find_asset(ctx) {
                    Reconcile::Adopt(ids) if ids.get("asset").map(String::as_str) == Some(remote_id) => Reconcile::Adopt(ids),
                    Reconcile::Adopt(_) => Reconcile::Conflict("that id is not the asset of this step".into()),
                    other => other,
                }
            }
            "publish-draft" => match self.published_state(ctx) {
                Reconcile::Adopt(ids) if ids.get("release").map(String::as_str) == Some(remote_id) => Reconcile::Adopt(ids),
                Reconcile::Adopt(_) => Reconcile::Conflict("that id is not this transaction's release".into()),
                Reconcile::NotThere => Reconcile::Conflict("the release is still a draft".into()),
                other => other,
            },
            other => Reconcile::Ambiguous(format!("the GitHub provider has no step of kind {other}")),
        }
    }

    fn lookup_hint(&self, ctx: &StepCtx) -> String {
        let repo = &ctx.plan.destination.repository;
        match ctx.op.kind.as_str() {
            "create-draft" => format!("open the releases of {repo} at GitHub and find the draft for tag {} whose text ends with \"{}\"; its id is the number in its edit address or in the API's list.", ctx.plan.destination.tag, Self::marker(ctx.plan)),
            "upload-asset" => format!("open the draft release of {repo} for tag {} and compare the asset {} with the planned size and SHA-256; its id is in the API's asset list.", ctx.plan.destination.tag, ctx.op.asset.as_deref().unwrap_or("")),
            _ => format!("open the release of {repo} for tag {} and see whether it is still a draft.", ctx.plan.destination.tag),
        }
    }
}

#[cfg(test)]
#[path = "github_tests.rs"]
mod tests;
