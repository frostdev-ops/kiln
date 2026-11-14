//! Tauri command: update checks for installed mods (kiln_manage::updates; docs/kiln/launcher/catalog.md, "Update
//! notices"). One command, `updates_check`, and one event, `kiln://mod-updates`, with the same payload (the
//! `CheckReport` of `kiln updates check --json`): the Catalog page and the main screen's mod list both read it.
//!
//! A check ends in a notice and a link. Nothing here downloads a mod, installs one, changes a profile or reloads
//! anything. GitHub sources are read anonymously (no credential can be sent: kiln_manage::updates::github); mods
//! installed from Nexus Mods are checked through the launcher's own catalog, with the player's sign-in when there is
//! one, as before.
//!
//! The request may carry one change of a mod's update source, applied before the check: a manual mapping (refused
//! with `conflict` when the mod is checked against another place and the change is not confirmed), the confirmation
//! of a source that changed by itself, or the removal of a mapping.

use crate::state::{blocking, dirs};
use kiln_manage::manifest::{ModKind, UpdatesSpec};
use kiln_manage::net::{self, Transport, WireRequest};
use kiln_manage::paths::DataDirs;
use kiln_manage::task::Progress;
use kiln_manage::updates::coordinator::{self, CheckItem, CheckOptions, CheckReport, UpdateProvider};
use kiln_manage::updates::github::{self, GithubProvider};
use kiln_manage::updates::nexus::NexusProvider;
use kiln_manage::updates::receipt;
use kiln_manage::updates::{ModUpdateStatus, Provider};
use kiln_manage::Error;
use serde::Deserialize;
use std::sync::Mutex;
use tauri::{AppHandle, Emitter};

/// Emitted after every check with its `CheckReport`.
pub const EVENT: &str = "kiln://mod-updates";

/// One check at a time in this launcher.
static CHECKING: Mutex<()> = Mutex::new(());

/// A change of one mod's update source.
#[derive(Debug, Clone, Deserialize)]
#[serde(tag = "action", rename_all = "camelCase")]
pub enum SourceChange {
    /// The player says where the mod's updates are. `spec`: the manifest's `updates` shape. `confirm`: replace another
    /// source the mod is checked against.
    Map { kind: ModKind, id: String, spec: serde_json::Value, #[serde(default)] confirm: bool },
    /// The mod's source changed (another repository in a new version's manifest, a new install): accept it.
    Confirm { kind: ModKind, id: String },
    /// Forget the player's mapping.
    Unmap { kind: ModKind, id: String },
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct UpdatesRequest {
    /// No request to any provider: what is remembered.
    pub offline: bool,
    /// Ask again although a recent answer is remembered (a provider's request limit still holds).
    pub refresh: bool,
    /// The daily background check: a source answered within the last day is not asked again.
    pub automatic: bool,
    /// The providers this check asks; the others answer from what is remembered, as in an offline check. None: all.
    /// The Catalog page asks Nexus Mods only, so opening it sends nothing to GitHub.
    pub providers: Option<Vec<Provider>>,
    pub source: Option<SourceChange>,
}

impl UpdatesRequest {
    /// Whether this check may send a request to `p`.
    fn asks(&self, p: Provider) -> bool {
        !self.offline && self.providers.as_ref().is_none_or(|l| l.contains(&p))
    }
}

/// A provider the request does not ask: it answers from what it remembers, with no request.
struct Remembered<'a>(&'a dyn UpdateProvider);

impl UpdateProvider for Remembered<'_> {
    fn provider(&self) -> Provider {
        self.0.provider()
    }
    fn check(&self, items: &[CheckItem], opts: &CheckOptions, progress: &mut dyn Progress) -> kiln_manage::Result<Vec<ModUpdateStatus>> {
        self.0.check(items, &CheckOptions { offline: true, refresh: false, automatic: false, ..opts.clone() }, progress)
    }
}

/// A manual mapping's source, read by the manifest's own rules (the frontend's value is not trusted to follow them).
fn read_spec(value: &serde_json::Value) -> Result<UpdatesSpec, Error> {
    let text = serde_json::json!({ "updates": value }).to_string();
    let root = kiln_manage::jsonc::parse_tree(&text).map_err(|e| Error::invalid(e.to_string()))?;
    match kiln_manage::manifest::parse_updates(&root) {
        (Some(spec), _) => Ok(spec),
        (None, why) => Err(Error::invalid(format!("That is not an update source: {}.", why.unwrap_or_else(|| "nothing was given".into())))),
    }
}

fn apply(dirs: &DataDirs, change: &SourceChange, now: i64) -> Result<(), Error> {
    match change {
        SourceChange::Map { kind, id, spec, confirm } => receipt::set_manual_source(dirs, *kind, id, &read_spec(spec)?, *confirm, now),
        SourceChange::Unmap { kind, id } => receipt::clear_manual_source(dirs, *kind, id).map(|_| ()),
        SourceChange::Confirm { kind, id } => {
            let target = coordinator::targets(dirs)?.into_iter().find(|t| t.library_ref.kind == *kind && t.library_ref.id == *id)
                .ok_or_else(|| Error::not_found(format!("{id} is not in the library.")))?;
            coordinator::confirm_source(dirs, &target, now).map(|_| ())
        }
    }
}

/// The command without Tauri: the source change, then the check with these providers.
pub fn run(dirs: &DataDirs, providers: &[&dyn UpdateProvider], request: &UpdatesRequest, now: i64) -> Result<CheckReport, Error> {
    if let Some(change) = &request.source {
        apply(dirs, change, now)?;
    }
    let opts = CheckOptions { offline: request.offline, refresh: request.refresh && !request.offline, automatic: request.automatic, now, ..CheckOptions::manual() };
    let remembered: Vec<Remembered> = providers.iter().map(|p| Remembered(*p)).collect();
    let providers: Vec<&dyn UpdateProvider> = providers.iter().zip(&remembered)
        .map(|(p, r)| if request.offline || request.asks(p.provider()) { *p } else { r as &dyn UpdateProvider })
        .collect();
    coordinator::check(dirs, &providers, &opts, &mut kiln_manage::task::NoProgress)
}

/// The transport of an offline check: it cannot send anything.
struct NoNetwork;

impl Transport for NoNetwork {
    fn exchange(&self, req: WireRequest) -> kiln_manage::Result<net::Response> {
        Err(net::network_error(&req.url))
    }
}

/// Looks for newer versions of the installed mods. Returns the result set and emits it as `kiln://mod-updates`.
#[tauri::command]
pub async fn updates_check(app: AppHandle, request: Option<UpdatesRequest>) -> Result<CheckReport, Error> {
    let request = request.unwrap_or_default();
    let report = blocking(move || {
        let _one = CHECKING.lock().unwrap_or_else(|e| e.into_inner());
        let dirs = dirs()?;
        let real = net::UreqTransport::with_timeouts(github::REQUEST_TIMEOUT, github::REQUEST_TIMEOUT);
        let transport: &dyn Transport = if request.asks(Provider::Github) { &real } else { &NoNetwork };
        let github = GithubProvider::new(&dirs, transport, &github::user_agent());
        // the launcher's own catalog (the player's sign-in, if any); none in a build with Nexus Mods off
        let catalog = super::nexus::the_catalog().ok();
        let nexus = NexusProvider::new(&dirs, catalog.as_deref());
        let providers: [&dyn UpdateProvider; 2] = [&github, &nexus];
        run(&dirs, &providers, &request, kiln_manage::util::now_unix())
    })
    .await?;
    let _ = app.emit(EVENT, &report);
    Ok(report)
}

#[cfg(test)]
mod tests {
    use super::*;
    use kiln_manage::ErrorCode;
    use kiln_manage::updates::{SourceOrigin, UnknownReason, UpdateState};

    const NOW: i64 = 1_800_000_000;

    /// A provider that answers "a newer version is out" for everything and remembers the options it was given.
    struct Fake(Provider, Mutex<Vec<(usize, CheckOptions)>>);

    impl UpdateProvider for Fake {
        fn provider(&self) -> Provider {
            self.0
        }
        fn check(&self, items: &[CheckItem], opts: &CheckOptions, _: &mut dyn Progress) -> kiln_manage::Result<Vec<ModUpdateStatus>> {
            self.1.lock().unwrap().push((items.len(), opts.clone()));
            Ok(items.iter().map(|i| {
                let mut s = i.status(false).with_state(UpdateState::Available);
                s.candidate_version = Some("9.9.9".into());
                s
            }).collect())
        }
    }

    struct Tmp(std::path::PathBuf);
    impl Drop for Tmp {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    /// A data folder with two mods: one whose manifest names a GitHub repository, one with no source.
    fn library(tag: &str) -> (Tmp, DataDirs) {
        let root = std::env::temp_dir().join(format!("kiln-launcher-updates-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        let dirs = DataDirs::at(root.join("data"));
        let mut folders = Vec::new();
        for (name, manifest) in [
            ("glow", r#"{"id": "lantern-glow", "version": "1.9.0", "apiVersion": "0.2", "updates": {"provider": "github", "repository": "example-org/lantern-glow"}}"#),
            ("local", r#"{"id": "local-tweaks", "version": "0.4.0", "apiVersion": "0.2"}"#),
        ] {
            std::fs::create_dir_all(root.join("src").join(name)).unwrap();
            std::fs::write(root.join("src").join(name).join("kiln.mod.json"), manifest).unwrap();
            folders.push(root.join("src").join(name));
        }
        assert_eq!(kiln_manage::library::import(&dirs, &folders, &mut kiln_manage::task::NoProgress).unwrap().imported.len(), 2);
        (Tmp(root), dirs)
    }

    fn of<'a>(r: &'a CheckReport, id: &str) -> &'a ModUpdateStatus {
        r.mods.iter().find(|m| m.library_ref.id == id).unwrap()
    }

    fn request(json: serde_json::Value) -> UpdatesRequest {
        serde_json::from_value(json).unwrap()
    }

    fn snapshot(dir: &std::path::Path) -> Vec<(String, Vec<u8>)> {
        fn walk(d: &std::path::Path, root: &std::path::Path, out: &mut Vec<(String, Vec<u8>)>) {
            for e in std::fs::read_dir(d).into_iter().flatten().flatten() {
                if e.path().is_dir() { walk(&e.path(), root, out) } else { out.push((e.path().strip_prefix(root).unwrap().display().to_string(), std::fs::read(e.path()).unwrap())) }
            }
        }
        let mut out = Vec::new();
        walk(dir, dir, &mut out);
        out.sort();
        out
    }

    #[test]
    fn the_request_becomes_the_checks_options_and_the_result_is_one_status_per_mod() {
        let (_t, dirs) = library("options");
        let gh = Fake(Provider::Github, Mutex::default());
        let r = run(&dirs, &[&gh], &UpdatesRequest::default(), NOW).unwrap();
        assert_eq!(r.mods.len(), 2);
        assert_eq!((of(&r, "lantern-glow").state, of(&r, "lantern-glow").candidate_version.as_deref()), (UpdateState::Available, Some("9.9.9")));
        assert_eq!((of(&r, "local-tweaks").state, of(&r, "local-tweaks").source.as_ref()), (UpdateState::Unsupported, None));
        run(&dirs, &[&gh], &request(serde_json::json!({"automatic": true})), NOW).unwrap();
        run(&dirs, &[&gh], &request(serde_json::json!({"refresh": true})), NOW).unwrap();
        // offline wins over refresh: an offline check never asks again
        let r = run(&dirs, &[&gh], &request(serde_json::json!({"offline": true, "refresh": true})), NOW).unwrap();
        assert!(r.offline);
        let seen: Vec<(usize, bool, bool, bool)> = gh.1.lock().unwrap().iter().map(|(n, o)| (*n, o.offline, o.refresh, o.automatic)).collect();
        assert_eq!(seen, [(1, false, false, false), (1, false, false, true), (1, false, true, false), (1, true, false, false)]);
        // the payload is the CLI's document: camelCase keys of CheckReport
        let v = serde_json::to_value(&r).unwrap();
        assert!(v["checkedAt"].is_string() && v["mods"][0]["libraryRef"]["id"].is_string() && v["mods"][0]["pageUrl"].is_null(), "{v}");
    }

    /// The Catalog's check asks Nexus Mods only: GitHub answers from what it remembers, with no request.
    #[test]
    fn a_provider_the_request_does_not_ask_is_checked_offline() {
        let (_t, dirs) = library("providers");
        let gh = Fake(Provider::Github, Mutex::default());
        let nx = Fake(Provider::Nexus, Mutex::default());
        // one mod on each provider: local-tweaks gets a Nexus Mods source by hand
        let to_nexus = request(serde_json::json!({"offline": true, "source": {"action": "map", "kind": "kiln", "id": "local-tweaks",
            "spec": {"provider": "nexus", "gameId": 5811, "modId": 316, "fileChainId": "90316"}}}));
        run(&dirs, &[&gh, &nx], &to_nexus, NOW).unwrap();
        let only_nexus = request(serde_json::json!({"providers": ["nexus"], "refresh": true}));
        assert!(only_nexus.asks(Provider::Nexus) && !only_nexus.asks(Provider::Github));
        run(&dirs, &[&gh, &nx], &only_nexus, NOW).unwrap();
        run(&dirs, &[&gh, &nx], &request(serde_json::json!({"providers": ["github", "nexus"]})), NOW).unwrap();
        run(&dirs, &[&gh, &nx], &UpdatesRequest::default(), NOW).unwrap();
        let seen = |f: &Fake| -> Vec<(usize, bool, bool)> { f.1.lock().unwrap().iter().map(|(n, o)| (*n, o.offline, o.refresh)).collect() };
        // the offline mapping, then: GitHub remembered only (no request, no refresh), asked, asked
        assert_eq!(seen(&gh), [(1, true, false), (1, true, false), (1, false, false), (1, false, false)]);
        assert_eq!(seen(&nx), [(1, true, false), (1, false, true), (1, false, false), (1, false, false)]);
        assert!(!request(serde_json::json!({"offline": true, "providers": ["nexus"]})).asks(Provider::Nexus));
        assert!(UpdatesRequest::default().asks(Provider::Github));
    }

    #[test]
    fn the_source_dialog_gets_the_complete_source_spec() {
        let (_t, dirs) = library("source-spec");
        let gh = Fake(Provider::Github, Mutex::default());
        let spec = serde_json::json!({"provider": "github", "repository": "example-org/lantern-glow",
            "tagPattern": "mods/glow/v{version}", "asset": "glow-{version}.zip", "channel": "prerelease"});
        let req = request(serde_json::json!({"offline": true, "source": {"action": "map", "kind": "kiln", "id": "lantern-glow", "spec": spec}}));
        let r = run(&dirs, &[&gh], &req, NOW).unwrap();
        let json = serde_json::to_value(of(&r, "lantern-glow")).unwrap();
        assert_eq!(json["source"]["spec"], spec, "opening and saving the dialog must preserve the source's tag, asset and channel");
    }

    #[test]
    fn a_mapping_is_read_by_the_manifests_rules_and_a_changed_source_needs_the_players_yes() {
        let (_t, dirs) = library("mapping");
        let gh = Fake(Provider::Github, Mutex::default());
        let map = |id: &str, spec: serde_json::Value, confirm: bool| request(serde_json::json!({"offline": true, "source": {"action": "map", "kind": "kiln", "id": id, "spec": spec, "confirm": confirm}}));
        // a mod without a source gets one from the player
        let r = run(&dirs, &[&gh], &map("local-tweaks", serde_json::json!({"provider": "github", "repository": "example-org/local-tweaks"}), false), NOW).unwrap();
        let s = of(&r, "local-tweaks").source.clone().unwrap();
        assert_eq!((s.destination.as_str(), s.origin), ("example-org/local-tweaks", SourceOrigin::Manual));
        // what is not a source by the manifest's rules is refused, and nothing is checked
        let asked = gh.1.lock().unwrap().len();
        for bad in [serde_json::json!({"provider": "github", "repository": "https://github.com/a/b"}), serde_json::json!({"provider": "github", "repository": "a/b", "url": "https://evil.example"}),
            serde_json::json!({"provider": "feed", "url": "https://evil.example/feed.json"}), serde_json::json!("example-org/x"), serde_json::Value::Null] {
            let e = run(&dirs, &[&gh], &map("local-tweaks", bad.clone(), true), NOW).unwrap_err();
            assert_eq!(e.code, ErrorCode::InvalidArgument, "{bad}");
        }
        assert_eq!(gh.1.lock().unwrap().len(), asked);
        // the authored mod was checked against its manifest's repository; another place needs a yes
        run(&dirs, &[&gh], &UpdatesRequest::default(), NOW).unwrap();
        let elsewhere = serde_json::json!({"provider": "github", "repository": "someone-else/lantern-glow"});
        let e = run(&dirs, &[&gh], &map("lantern-glow", elsewhere.clone(), false), NOW).unwrap_err();
        assert_eq!(e.code, ErrorCode::Conflict);
        let r = run(&dirs, &[&gh], &request(serde_json::json!({"offline": true})), NOW).unwrap();
        assert_eq!(of(&r, "lantern-glow").source.as_ref().unwrap().destination, "example-org/lantern-glow", "nothing changed without the yes");
        let r = run(&dirs, &[&gh], &map("lantern-glow", elsewhere, true), NOW).unwrap();
        assert_eq!(of(&r, "lantern-glow").source.as_ref().unwrap().destination, "someone-else/lantern-glow");
        // the mapping removed: the manifest's source is another place than the accepted one, so it waits for a yes
        let unmap = request(serde_json::json!({"offline": true, "source": {"action": "unmap", "kind": "kiln", "id": "lantern-glow"}}));
        let r = run(&dirs, &[&gh], &unmap, NOW).unwrap();
        assert_eq!((of(&r, "lantern-glow").state, of(&r, "lantern-glow").reason), (UpdateState::Unknown, Some(UnknownReason::SourceConflict)));
        let confirm = request(serde_json::json!({"offline": true, "source": {"action": "confirm", "kind": "kiln", "id": "lantern-glow"}}));
        let r = run(&dirs, &[&gh], &confirm, NOW).unwrap();
        let s = of(&r, "lantern-glow");
        assert_eq!((s.state, s.source.as_ref().unwrap().destination.as_str(), s.source.as_ref().unwrap().origin), (UpdateState::Available, "example-org/lantern-glow", SourceOrigin::Manifest));
        // a mod that is not there
        let ghost = request(serde_json::json!({"source": {"action": "confirm", "kind": "kiln", "id": "ghost"}}));
        assert_eq!(run(&dirs, &[&gh], &ghost, NOW).unwrap_err().code, ErrorCode::NotFound);
    }

    /// The launcher half of "no auto install" on the Rust side: a check that finds updates leaves the library and the
    /// profiles byte for byte as they were.
    #[test]
    fn a_check_that_finds_updates_changes_nothing_but_its_own_files() {
        let (_t, dirs) = library("no-install");
        let gh = Fake(Provider::Github, Mutex::default());
        let before = snapshot(&dirs.library());
        for req in [UpdatesRequest::default(), request(serde_json::json!({"automatic": true})), request(serde_json::json!({"refresh": true}))] {
            let r = run(&dirs, &[&gh], &req, NOW).unwrap();
            assert_eq!(of(&r, "lantern-glow").state, UpdateState::Available);
        }
        assert_eq!(snapshot(&dirs.library()), before);
        assert!(!dirs.profiles().exists());
        let mut top: Vec<String> = std::fs::read_dir(&dirs.root).unwrap().flatten().map(|e| e.file_name().to_string_lossy().into_owned()).collect();
        top.sort();
        assert_eq!(top, ["library", "updates"]);
    }
}
