//! The Nexus Mods provider: an adapter over the catalog's own update check ([`Catalog::check_updates`],
//! [`Catalog::installed`]). It adds no request and no rule: which upload is newer, the 30-minute memory per file and
//! the need to be signed in are the catalog's, as before. This path uses the player's Nexus Mods account, so its
//! statuses say `authenticated: true`; it is not an anonymous check and is never labelled as one.
//!
//! - Nexus Mods switched off in this build ([`crate::nexus::enabled`]), or no catalog given: `disabled`.
//! - Not signed in: no request; an "update available" seen earlier is kept and marked stale, everything else is
//!   `unknown / network`.
//! - A mod whose Nexus source has no install record of the catalog (a manifest or a manual mapping that names a
//!   Nexus file, the mod itself imported by hand): `unknown / not_checked` with the mod's page. The catalog's check
//!   only knows what it installed.

use super::coordinator::{CheckItem, CheckOptions, UpdateProvider};
use super::status::{CandidateIdentity, Coverage, ModUpdateStatus, Provider, UnknownReason, UpdateState};
use super::STALE_AFTER_SECS;
use crate::manifest::UpdatesSpec;
use crate::nexus::catalog::{self, Catalog, InstalledMod};
use crate::paths::DataDirs;
use crate::task::Progress;
use crate::{ErrorCode, Result};
use std::collections::HashMap;

pub struct NexusProvider<'a> {
    dirs: &'a DataDirs,
    catalog: Option<&'a Catalog>,
}

impl<'a> NexusProvider<'a> {
    /// `catalog`: the launcher's catalog (with the player's sign-in, if any). None: Nexus Mods is not available here.
    pub fn new(dirs: &'a DataDirs, catalog: Option<&'a Catalog>) -> NexusProvider<'a> {
        NexusProvider { dirs, catalog }
    }

    /// When the catalog last asked Nexus Mods about each file (`nexus/latest.json`, read only).
    fn checked_times(&self) -> HashMap<String, i64> {
        let path = catalog::root_of(self.dirs).join("latest.json");
        let v = crate::util::read_json::<serde_json::Value>(&path).ok().flatten().unwrap_or_default();
        v.as_object().into_iter().flatten().filter_map(|(file, l)| Some((file.clone(), l.get("checked")?.as_i64()?))).collect()
    }
}

fn page(spec: &UpdatesSpec) -> Option<String> {
    match spec {
        UpdatesSpec::Nexus { mod_id, .. } => Some(catalog::files_url(&mod_id.to_string(), None)),
        UpdatesSpec::Github { .. } => None,
    }
}

impl UpdateProvider for NexusProvider<'_> {
    fn provider(&self) -> Provider {
        Provider::Nexus
    }

    fn check(&self, items: &[CheckItem], opts: &CheckOptions, _progress: &mut dyn Progress) -> Result<Vec<ModUpdateStatus>> {
        let Some(cat) = self.catalog.filter(|_| crate::nexus::enabled()) else {
            return Ok(items.iter().map(|i| i.status(true).with_state(UpdateState::Disabled)).collect());
        };
        // What the catalog knows, and why it is not from now (None: it is).
        let (mods, failure): (Vec<InstalledMod>, Option<UnknownReason>) = if opts.offline {
            (cat.installed(self.dirs).unwrap_or_default(), None)
        } else if !cat.client().has_credentials() {
            (cat.installed(self.dirs).unwrap_or_default(), Some(UnknownReason::Network))
        } else {
            match cat.check_updates(self.dirs, opts.refresh) {
                Ok(m) => (m, None),
                Err(e) => {
                    let why = if e.code == ErrorCode::Busy { UnknownReason::RateLimited } else { UnknownReason::Network };
                    (cat.installed(self.dirs).unwrap_or_default(), Some(why))
                }
            }
        };
        let times = self.checked_times();
        Ok(items.iter().map(|item| {
            let mut s = item.status(true);
            s.page_url = page(&item.spec);
            let Some(m) = mods.iter().find(|m| m.record.refs.contains(&item.target.library_ref)
                && matches!(&item.spec, UpdatesSpec::Nexus { game_id, mod_id, file_chain_id }
                    if *game_id == crate::manifest::NEXUS_GAME_ID && m.record.mod_id == mod_id.to_string() && m.record.file_id == *file_chain_id)) else {
                return s.unknown(UnknownReason::NotChecked);
            };
            // Nexus versions are free text: the installed one is shown as Nexus names it, beside the candidate's
            s.installed_version = m.record.version.clone();
            let Some(latest) = &m.info.latest_version else {
                return s.unknown(failure.unwrap_or(if opts.offline { UnknownReason::NotChecked } else { UnknownReason::Network }));
            };
            let checked = times.get(&m.record.file_id).copied();
            s.candidate_version = Some(latest.clone());
            s.candidate_identity = Some(CandidateIdentity { file_version_id: m.info.latest_gsid.clone(), ..Default::default() });
            s.coverage = Coverage::Complete;
            s.checked_at = checked.map(crate::util::rfc3339);
            let state = if !m.info.update_available {
                UpdateState::Current
            } else if crate::nexus::version::is_newer(latest, &m.record.version) {
                UpdateState::Available
            } else {
                // another upload of the same version (the catalog's rule counts it)
                UpdateState::NewUpload
            };
            s = s.with_state(state);
            // fresh: answered within the last day, by a clock that has not gone backwards since
            let fresh = checked.is_some_and(|c| c <= opts.now && opts.now - c <= STALE_AFTER_SECS);
            match failure {
                Some(why) => s.into_stale(why),
                None if !fresh => s.into_stale(UnknownReason::Network),
                None => s,
            }
        }).collect())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::library::LibraryRef;
    use crate::manifest::ModKind;
    use crate::nexus::api::{AppId, Client, HttpRequest, HttpResponse, HttpStream, Transport};
    use crate::task::NoProgress;
    use crate::updates::coordinator::Target;
    use crate::updates::status::SourceOrigin;
    use std::path::PathBuf;
    use std::sync::{Arc, Mutex};

    const NOW: i64 = 1_800_000_000;

    /// A Nexus Mods that knows one file's versions, counts what it is asked and can be switched to fail.
    #[derive(Default)]
    struct Mock {
        versions: Mutex<serde_json::Value>,
        log: Mutex<Vec<(String, bool)>>,
        fail: Mutex<Option<u16>>,
    }

    impl Transport for Mock {
        fn send(&self, req: &HttpRequest) -> Result<HttpResponse> {
            let auth = req.headers.iter().any(|(k, _)| k.eq_ignore_ascii_case("authorization"));
            self.log.lock().unwrap().push((req.url.clone(), auth));
            if let Some(status) = *self.fail.lock().unwrap() {
                return Ok(HttpResponse { status, headers: vec![("Retry-After".into(), "600".into())], body: vec![] });
            }
            let body = serde_json::json!({"data": {"versions": self.versions.lock().unwrap().clone()}});
            Ok(HttpResponse { status: 200, headers: vec![("content-type".into(), "application/json".into())], body: serde_json::to_vec(&body).unwrap() })
        }
        fn open(&self, _: &str, _: &[(String, String)]) -> Result<HttpStream> {
            panic!("an update check opened a download")
        }
    }

    fn version(vgsid: &str, version: &str, position: &str) -> serde_json::Value {
        serde_json::json!({"id": format!("80{vgsid}2"), "file": {"id": "90310", "name": "Main file"}, "position": position, "game_scoped_id": vgsid,
            "name": format!("lantern-glow-{version}.zip"), "version": version, "category": "main", "uploaded_at": "2026-11-02T00:00:00Z", "manifest_url": null})
    }

    struct Env {
        root: PathBuf,
        dirs: DataDirs,
        mock: Arc<Mock>,
        item: CheckItem,
    }

    /// A data folder with one Kiln mod in the library and the catalog's install record of it (version 1.2.0, upload
    /// 1001 of file 90310 on page 310).
    fn env(tag: &str) -> Env {
        let root = std::env::temp_dir().join(format!("kiln-updates-nexus-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        let dirs = DataDirs::at(root.join("data"));
        let src = root.join("src/lantern-glow");
        std::fs::create_dir_all(&src).unwrap();
        std::fs::write(src.join("kiln.mod.json"), r#"{"id": "lantern-glow", "name": "Lantern Glow", "version": "1.2.0", "apiVersion": "0.2"}"#).unwrap();
        let report = crate::library::import(&dirs, &[src], &mut NoProgress).unwrap();
        let lref: LibraryRef = report.imported[0].clone();
        let nexus = catalog::root_of(&dirs);
        std::fs::create_dir_all(&nexus).unwrap();
        std::fs::write(nexus.join("installs.json"), serde_json::to_vec(&serde_json::json!({"format": 1, "mods": [{
            "modId": "310", "name": "Lantern Glow", "fileId": "90310", "fileName": "Main file", "versionId": "8010012", "versionGsid": "1001",
            "version": "1.2.0", "refs": [lref], "installed": "2026-11-07T10:00:00Z", "source": "nxm"}]})).unwrap()).unwrap();
        let spec = UpdatesSpec::Nexus { game_id: 5811, mod_id: 310, file_chain_id: "90310".into() };
        let target = Target { library_ref: lref, name: "Lantern Glow".into(), mod_id: "lantern-glow".into(), side: None, authored: None };
        Env { root, dirs, mock: Arc::new(Mock::default()), item: CheckItem { target, spec, origin: SourceOrigin::Receipt, receipt: None } }
    }

    impl Env {
        fn catalog(&self, token: Option<&str>) -> Catalog {
            Catalog::new(catalog::root_of(&self.dirs), Client::new(self.mock.clone(), token.map(str::to_string), AppId::kiln_launcher("0.0.0-test")))
        }
        fn check(&self, cat: Option<&Catalog>, f: impl FnOnce(&mut CheckOptions)) -> ModUpdateStatus {
            let mut opts = CheckOptions { offline: false, refresh: false, automatic: false, now: crate::util::now_unix(), api_version: "0.2.3".into() };
            f(&mut opts);
            let s = NexusProvider::new(&self.dirs, cat).check(std::slice::from_ref(&self.item), &opts, &mut NoProgress).unwrap().remove(0);
            assert!(s.is_consistent(), "{s:?}");
            assert!(s.source.as_ref().is_some_and(|x| x.authenticated && x.provider == Provider::Nexus), "the Nexus path is the signed-in one");
            s
        }
        fn requests(&self) -> usize {
            self.mock.log.lock().unwrap().len()
        }
    }

    impl Drop for Env {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.root);
        }
    }

    #[test]
    fn the_catalogs_check_comes_back_as_statuses() {
        let e = env("states");
        let cat = e.catalog(Some("TOKEN"));
        // the installed upload is the newest
        *e.mock.versions.lock().unwrap() = serde_json::json!([version("1001", "1.2.0", "2")]);
        let s = e.check(Some(&cat), |_| {});
        assert_eq!((s.state, s.candidate_version.as_deref(), s.coverage, s.stale, s.reason), (UpdateState::Current, Some("1.2.0"), Coverage::Complete, false, None));
        assert_eq!(s.page_url.as_deref(), Some("https://www.nexusmods.com/enshrouded/mods/310?tab=files"));
        assert!(s.checked_at.is_some());
        // within the catalog's 30 minutes nothing is asked again; --refresh asks
        let n = e.requests();
        e.check(Some(&cat), |_| {});
        assert_eq!(e.requests(), n);
        // a higher version in the same file
        *e.mock.versions.lock().unwrap() = serde_json::json!([version("1001", "1.2.0", "2"), version("1009", "1.3.0", "3")]);
        let s = e.check(Some(&cat), |o| o.refresh = true);
        assert_eq!(e.requests(), n + 1);
        assert_eq!((s.state, s.candidate_version.as_deref(), s.installed_version.as_str()), (UpdateState::Available, Some("1.3.0"), "1.2.0"));
        assert_eq!(s.candidate_identity.unwrap().file_version_id.as_deref(), Some("1009"));
        // the same version uploaded again: a new upload, not a version upgrade
        *e.mock.versions.lock().unwrap() = serde_json::json!([version("1001", "1.2.0", "2"), version("1010", "1.2.0", "4")]);
        let s = e.check(Some(&cat), |o| o.refresh = true);
        assert_eq!((s.state, s.candidate_version.as_deref()), (UpdateState::NewUpload, Some("1.2.0")));
        // every request went to the file-chain endpoint with the account's authorization: this is not the anonymous path
        let log = e.mock.log.lock().unwrap();
        assert!(log.iter().all(|(url, auth)| url.contains("/mod-files/90310/versions") && *auth), "{log:?}");
    }

    #[test]
    fn without_a_sign_in_nothing_is_asked_and_nothing_reads_as_current() {
        let e = env("signed-out");
        let signed_out = e.catalog(None);
        let s = e.check(Some(&signed_out), |_| {});
        assert_eq!((s.state, s.reason, e.requests()), (UpdateState::Unknown, Some(UnknownReason::Network), 0));
        // signed in once: current. Then signed out again: the remembered "current" is not shown as such
        *e.mock.versions.lock().unwrap() = serde_json::json!([version("1001", "1.2.0", "2")]);
        assert_eq!(e.check(Some(&e.catalog(Some("TOKEN"))), |_| {}).state, UpdateState::Current);
        let n = e.requests();
        let s = e.check(Some(&signed_out), |_| {});
        assert_eq!((s.state, s.reason, s.stale, e.requests()), (UpdateState::Unknown, Some(UnknownReason::Network), true, n));
        // a remembered "update available" is kept, marked stale
        *e.mock.versions.lock().unwrap() = serde_json::json!([version("1001", "1.2.0", "2"), version("1009", "1.3.0", "3")]);
        assert_eq!(e.check(Some(&e.catalog(Some("TOKEN"))), |o| o.refresh = true).state, UpdateState::Available);
        let s = e.check(Some(&signed_out), |_| {});
        assert_eq!((s.state, s.stale, s.reason, s.candidate_version.as_deref()), (UpdateState::Available, true, Some(UnknownReason::Network), Some("1.3.0")));
    }

    #[test]
    fn a_failed_or_offline_check_never_reads_as_current() {
        let e = env("failing");
        let cat = e.catalog(Some("TOKEN"));
        // offline with nothing remembered
        let s = e.check(Some(&cat), |o| o.offline = true);
        assert_eq!((s.state, s.reason, e.requests()), (UpdateState::Unknown, Some(UnknownReason::NotChecked), 0));
        *e.mock.versions.lock().unwrap() = serde_json::json!([version("1001", "1.2.0", "2")]);
        assert_eq!(e.check(Some(&cat), |_| {}).state, UpdateState::Current);
        let n = e.requests();
        // offline right after: the answer is fresh, and no request is made
        let s = e.check(Some(&cat), |o| o.offline = true);
        assert_eq!((s.state, s.stale, e.requests()), (UpdateState::Current, false, n));
        // offline two days later: stale, so not "current"
        let s = e.check(Some(&cat), |o| { o.offline = true; o.now += 2 * STALE_AFTER_SECS; });
        assert_eq!((s.state, s.stale, s.reason), (UpdateState::Unknown, true, Some(UnknownReason::Network)));
        // the clock went backwards: the remembered answer cannot be dated, so it is stale too
        let s = e.check(Some(&cat), |o| { o.offline = true; o.now -= 3600; });
        assert_eq!((s.state, s.stale), (UpdateState::Unknown, true));
        // Nexus answers 500, then 429
        *e.mock.fail.lock().unwrap() = Some(500);
        let s = e.check(Some(&cat), |o| o.refresh = true);
        assert_eq!((s.state, s.stale, s.reason), (UpdateState::Unknown, true, Some(UnknownReason::Network)));
        *e.mock.fail.lock().unwrap() = Some(429);
        let s = e.check(Some(&cat), |o| o.refresh = true);
        assert_eq!((s.state, s.stale, s.reason), (UpdateState::Unknown, true, Some(UnknownReason::RateLimited)));
    }

    #[test]
    fn a_manual_mapping_never_uses_another_nexus_chain() {
        let mut e = env("mapped-chain");
        let cat = e.catalog(Some("TOKEN"));
        *e.mock.versions.lock().unwrap() = serde_json::json!([version("1009", "1.3.0", "3")]);
        assert_eq!(e.check(Some(&cat), |_| {}).state, UpdateState::Available);
        let requests = e.requests();
        for spec in [
            UpdatesSpec::Nexus { game_id: 5811, mod_id: 310, file_chain_id: "90311".into() },
            UpdatesSpec::Nexus { game_id: 5811, mod_id: 311, file_chain_id: "90310".into() },
        ] {
            e.item.spec = spec;
            e.item.origin = SourceOrigin::Manual;
            let s = e.check(Some(&cat), |o| o.offline = true);
            assert_eq!((s.state, s.reason, s.candidate_version), (UpdateState::Unknown, Some(UnknownReason::NotChecked), None));
        }
        assert_eq!(e.requests(), requests);
    }

    #[test]
    fn no_catalog_means_disabled_and_an_unrecorded_nexus_source_is_not_checked() {
        let e = env("disabled");
        let s = e.check(None, |_| {});
        assert_eq!((s.state, s.reason, e.requests()), (UpdateState::Disabled, None, 0));
        // a mod that names a Nexus file in its manifest but was not installed by the catalog
        let cat = e.catalog(Some("TOKEN"));
        let mut other = e.item.clone();
        other.target.library_ref = LibraryRef { kind: ModKind::Kiln, id: "hand-made".into(), version: "1.0.0".into(), hash: "kdh1:none".into() };
        other.origin = SourceOrigin::Manifest;
        let opts = CheckOptions { offline: true, refresh: false, automatic: false, now: NOW, api_version: "0.2.3".into() };
        let s = NexusProvider::new(&e.dirs, Some(&cat)).check(&[other], &opts, &mut NoProgress).unwrap().remove(0);
        assert_eq!((s.state, s.reason), (UpdateState::Unknown, Some(UnknownReason::NotChecked)));
        assert_eq!(s.page_url.as_deref(), Some("https://www.nexusmods.com/enshrouded/mods/310?tab=files"));
    }
}
