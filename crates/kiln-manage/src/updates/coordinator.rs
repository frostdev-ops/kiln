//! The coordinator: one check over every installed mod, whatever its provider.
//!
//! For each mod it decides the source (the player's manual mapping, else what an install receipt proves, else the
//! `updates` its manifest declares; nothing else, never a guess from a name or a homepage), holds a changed source
//! back until the player confirms it, hands the mods of each provider to that provider one provider after the
//! other, and returns one [`CheckReport`]. Providers are passed in: a provider that is not there (switched off in
//! this build) makes its mods `disabled`, not an error.

use super::receipt::{self, ReceiptFile, SourceIdentity, SourceReceipt};
use super::status::{ModUpdateStatus, Provider, SourceInfo, SourceOrigin, UnknownReason, UpdateState};
use crate::library::LibraryRef;
use crate::manifest::{ModKind, Side, UpdatesSpec};
use crate::paths::DataDirs;
use crate::task::Progress;
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};

/// An installed mod as a check sees it: the newest version of a library entry.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Target {
    pub library_ref: LibraryRef,
    pub name: String,
    /// The id the mod's own manifest gives (what a release's `kiln-release.json` names); the library id when the
    /// manifest has none.
    pub mod_id: String,
    pub side: Option<Side>,
    /// The `updates` of its manifest, when it has a readable one (kiln.mod.json and native.json).
    pub authored: Option<UpdatesSpec>,
}

/// One mod with the source it is checked against: what a provider gets.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CheckItem {
    pub target: Target,
    pub spec: UpdatesSpec,
    pub origin: SourceOrigin,
    /// What Kiln recorded when this library entry was installed or imported, if anything.
    pub receipt: Option<SourceReceipt>,
}

impl CheckItem {
    pub fn source_info(&self, authenticated: bool) -> SourceInfo {
        let id = SourceIdentity::of(&self.spec);
        let destination = match &self.spec {
            // as the author or the player wrote it; the identity's lower-case form is only for comparing
            UpdatesSpec::Github { repository, .. } => repository.clone(),
            UpdatesSpec::Nexus { .. } => id.destination,
        };
        SourceInfo { provider: id.provider, destination, origin: self.origin, authenticated, spec: Some(self.spec.clone()) }
    }

    /// The item's status before anything is known: `unknown / not_checked`.
    pub fn status(&self, authenticated: bool) -> ModUpdateStatus {
        ModUpdateStatus::new(self.target.library_ref.clone(), &self.target.name, Some(self.source_info(authenticated)))
    }
}

/// How one check runs.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CheckOptions {
    /// No request at all: what is remembered, marked stale where it is old.
    pub offline: bool,
    /// Ask again although a recent answer is remembered. Never gets around a provider's request limit.
    pub refresh: bool,
    /// The daily background check: a source answered within the last day is not asked again.
    pub automatic: bool,
    /// Unix seconds.
    pub now: i64,
    /// The Kiln mod API version a candidate's declared range is compared with.
    pub api_version: String,
}

impl CheckOptions {
    /// A check the player asked for, now.
    pub fn manual() -> CheckOptions {
        CheckOptions { offline: false, refresh: false, automatic: false, now: crate::util::now_unix(), api_version: crate::profile::KILN_API.to_string() }
    }
}

/// One provider: GitHub, Nexus Mods.
pub trait UpdateProvider {
    fn provider(&self) -> Provider;
    /// One status per item, in the items' order. A failed request is a status (`unknown`, or stale), not an `Err`;
    /// `Err` is for a canceled check and for local failures (a file that cannot be written).
    fn check(&self, items: &[CheckItem], opts: &CheckOptions, progress: &mut dyn Progress) -> Result<Vec<ModUpdateStatus>>;
}

/// The result set of one check: the value behind `kiln updates check --json` and the launcher's list.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CheckReport {
    pub format: u32,
    /// When this check ran (RFC 3339, UTC).
    pub checked_at: String,
    /// The check made no request (`--offline`).
    pub offline: bool,
    /// One per installed mod, by kind and id.
    pub mods: Vec<ModUpdateStatus>,
}

fn authored(kind: ModKind, folder: &std::path::Path) -> Option<UpdatesSpec> {
    let file = match kind {
        ModKind::Kiln => crate::manifest::KILN_MANIFEST,
        ModKind::Native => crate::manifest::NATIVE_MANIFEST,
        // other engines' manifests have no such key: those mods need a mapping or a receipt
        _ => return None,
    };
    let text = crate::manifest::read_text(&folder.join(file)).ok()?;
    let root = crate::jsonc::parse_tree(&text).ok()?;
    crate::manifest::parse_updates(&root).0
}

/// The installed mods: the newest version of every library entry.
pub fn targets(dirs: &DataDirs) -> Result<Vec<Target>> {
    let mut out = Vec::new();
    for e in crate::library::list(dirs, None)? {
        let Some(v) = e.versions.first() else { continue };
        out.push(Target {
            library_ref: LibraryRef { kind: e.kind, id: e.id.clone(), version: v.version.clone(), hash: v.hash.clone() },
            name: e.name.clone(),
            mod_id: v.manifest_id.clone().unwrap_or_else(|| e.id.clone()),
            side: v.side,
            authored: authored(e.kind, &v.folder),
        });
    }
    Ok(out)
}

/// What a mod's source is, before the player's confirmation is looked at: manual mapping, then receipt, then
/// manifest.
fn candidate(file: &ReceiptFile, t: &Target) -> Option<(UpdatesSpec, SourceOrigin)> {
    let r = &t.library_ref;
    if let Some(spec) = file.manual(r.kind, &r.id) {
        return Some((spec.clone(), SourceOrigin::Manual));
    }
    if let Some(spec) = file.receipt(r).and_then(SourceReceipt::source) {
        return Some((spec, SourceOrigin::Receipt));
    }
    t.authored.clone().map(|spec| (spec, SourceOrigin::Manifest))
}

enum Resolution {
    Unsupported,
    Source(CheckItem),
    /// The source is at another place than the one accepted for the mod: held back until confirmed.
    Conflict(CheckItem),
}

fn resolve(file: &mut ReceiptFile, t: &Target, now: i64, changed: &mut bool) -> Resolution {
    let Some((spec, origin)) = candidate(file, t) else { return Resolution::Unsupported };
    let r = &t.library_ref;
    let item = CheckItem { target: t.clone(), spec, origin, receipt: file.receipt(r).cloned() };
    let identity = SourceIdentity::of(&item.spec);
    match file.source(r.kind, &r.id) {
        Some(accepted) if accepted.identity != identity => Resolution::Conflict(item),
        Some(_) => Resolution::Source(item),
        // seen for the first time: this is the mod's source from now on
        None => {
            file.accept(r.kind, &r.id, &item.spec, origin, now);
            *changed = true;
            Resolution::Source(item)
        }
    }
}

/// Accepts the mod's present source after it changed (the player's "yes, check it there from now on"). Returns the
/// accepted place, or None when the mod has no source at all.
pub fn confirm_source(dirs: &DataDirs, t: &Target, now: i64) -> Result<Option<SourceIdentity>> {
    receipt::update(dirs, |file| {
        let Some((spec, origin)) = candidate(file, t) else { return (false, None) };
        file.accept(t.library_ref.kind, &t.library_ref.id, &spec, origin, now);
        (true, Some(SourceIdentity::of(&spec)))
    })
}

/// A provider's answer may never break the type's rules; one that does is a bug there, and is shown as "not
/// checked" rather than trusted.
fn sanitize(s: ModUpdateStatus, item: &CheckItem) -> ModUpdateStatus {
    if s.is_consistent() && s.library_ref == item.target.library_ref {
        s
    } else {
        item.status(false)
    }
}

/// Checks every installed mod.
pub fn check(dirs: &DataDirs, providers: &[&dyn UpdateProvider], opts: &CheckOptions, progress: &mut dyn Progress) -> Result<CheckReport> {
    check_targets(dirs, targets(dirs)?, providers, opts, progress)
}

/// Checks these mods.
pub fn check_targets(dirs: &DataDirs, targets: Vec<Target>, providers: &[&dyn UpdateProvider], opts: &CheckOptions, progress: &mut dyn Progress) -> Result<CheckReport> {
    // Installs made before receipts existed. A catalog file that cannot be read costs those mods their receipt, not
    // the check.
    let _ = receipt::migrate_nexus_installs(dirs);
    let resolved: Vec<Resolution> = receipt::update(dirs, |file| {
        let mut changed = false;
        let all = targets.iter().map(|t| resolve(file, t, opts.now, &mut changed)).collect();
        (changed, all)
    })?;

    let mut statuses: Vec<Option<ModUpdateStatus>> = Vec::with_capacity(resolved.len());
    let mut items: Vec<(usize, CheckItem)> = Vec::new();
    for (i, (t, r)) in targets.iter().zip(resolved).enumerate() {
        statuses.push(match r {
            Resolution::Unsupported => {
                let mut s = ModUpdateStatus::new(t.library_ref.clone(), &t.name, None).with_state(UpdateState::Unsupported);
                s.source = None;
                Some(s)
            }
            Resolution::Conflict(item) => Some(item.status(false).unknown(UnknownReason::SourceConflict)),
            Resolution::Source(item) => {
                items.push((i, item));
                None
            }
        });
    }
    // one provider after the other, never two at once
    for p in [Provider::Github, Provider::Nexus] {
        if progress.canceled() {
            return Err(Error::new(ErrorCode::Canceled, "canceled"));
        }
        let (at, batch): (Vec<usize>, Vec<CheckItem>) = items.iter().filter(|(_, it)| SourceIdentity::of(&it.spec).provider == p).cloned().unzip();
        if batch.is_empty() {
            continue;
        }
        let answers = match providers.iter().find(|x| x.provider() == p) {
            Some(provider) => {
                let got = provider.check(&batch, opts, progress)?;
                if got.len() != batch.len() {
                    return Err(Error::new(ErrorCode::Internal, format!("the {} provider answered for {} of {} mods", p.as_str(), got.len(), batch.len())));
                }
                got.into_iter().zip(&batch).map(|(s, item)| sanitize(s, item)).collect()
            }
            None => batch.iter().map(|item| item.status(false).with_state(UpdateState::Disabled)).collect::<Vec<_>>(),
        };
        for (i, s) in at.into_iter().zip(answers) {
            statuses[i] = Some(s);
        }
    }
    let mods = statuses.into_iter().flatten().collect();
    Ok(CheckReport { format: 1, checked_at: crate::util::rfc3339(opts.now), offline: opts.offline, mods })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::manifest::UpdateChannel;
    use crate::task::NoProgress;
    use std::path::PathBuf;
    use std::sync::Mutex;

    fn tmp(tag: &str) -> (DataDirs, PathBuf) {
        let d = std::env::temp_dir().join(format!("kiln-updates-coord-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(&d).unwrap();
        (DataDirs::at(d.join("data")), d)
    }

    fn github(repo: &str) -> UpdatesSpec {
        UpdatesSpec::Github { repository: repo.into(), tag_pattern: "v{version}".into(), asset: None, channel: UpdateChannel::Stable }
    }

    fn target(kind: ModKind, id: &str, version: &str, authored: Option<UpdatesSpec>) -> Target {
        Target { library_ref: LibraryRef { kind, id: id.into(), version: version.into(), hash: format!("kdh1:{id}-{version}") }, name: id.to_uppercase(), mod_id: id.into(), side: None, authored }
    }

    fn opts() -> CheckOptions {
        CheckOptions { offline: false, refresh: false, automatic: false, now: 1_800_000_000, api_version: "0.2.3".into() }
    }

    /// A provider that records what it was asked and answers `current` for everything.
    struct Recorder(Provider, Mutex<Vec<Vec<CheckItem>>>);
    impl UpdateProvider for Recorder {
        fn provider(&self) -> Provider {
            self.0
        }
        fn check(&self, items: &[CheckItem], _: &CheckOptions, _: &mut dyn Progress) -> Result<Vec<ModUpdateStatus>> {
            self.1.lock().unwrap().push(items.to_vec());
            Ok(items.iter().map(|i| i.status(false).with_state(UpdateState::Current)).collect())
        }
    }

    fn of<'a>(r: &'a CheckReport, id: &str) -> &'a ModUpdateStatus {
        r.mods.iter().find(|m| m.library_ref.id == id).unwrap_or_else(|| panic!("no status for {id}"))
    }

    #[test]
    fn an_unmapped_mod_is_unsupported_never_guessed() {
        let (dirs, root) = tmp("unsupported");
        let gh = Recorder(Provider::Github, Mutex::default());
        // an engine mod with a homepage in its own manifest, a Kiln mod without "updates": no source
        let targets = vec![target(ModKind::Eml, "auto-loot", "1.0.0", None), target(ModKind::Kiln, "plain", "0.1.0", None)];
        let r = check_targets(&dirs, targets, &[&gh], &opts(), &mut NoProgress).unwrap();
        assert_eq!(r.mods.len(), 2);
        for s in &r.mods {
            assert_eq!((s.state, s.source.as_ref(), s.reason, s.candidate_version.as_ref()), (UpdateState::Unsupported, None, None, None));
            assert!(s.is_consistent());
        }
        assert!(gh.1.lock().unwrap().is_empty(), "no provider is asked about a mod without a source");
        assert!(receipt::load(&dirs).unwrap().sources.is_empty());
        let _ = std::fs::remove_dir_all(root);
    }

    #[test]
    fn the_source_is_the_manual_mapping_then_the_receipt_then_the_manifest() {
        let (dirs, root) = tmp("precedence");
        let (gh, nx) = (Recorder(Provider::Github, Mutex::default()), Recorder(Provider::Nexus, Mutex::default()));
        let authored = target(ModKind::Kiln, "authored", "1.0.0", Some(github("author/authored")));
        let from_nexus = target(ModKind::Kiln, "from-nexus", "1.0.0", Some(github("author/from-nexus")));
        let mapped = target(ModKind::Kiln, "mapped", "1.0.0", Some(github("author/mapped")));
        // from-nexus was installed from Nexus Mods (a receipt); mapped has a receipt and a manual mapping
        let rec = |t: &Target, m: &str, chain: &str| serde_json::from_value(serde_json::json!({"modId": m, "name": t.name, "fileId": chain, "fileName": "Main",
            "versionId": "v1", "versionGsid": "1", "version": "1.0.0", "refs": [t.library_ref], "installed": "2026-11-01T00:00:00Z", "source": "nxm"})).unwrap();
        receipt::capture_nexus(&dirs, &rec(&from_nexus, "310", "90310")).unwrap();
        receipt::capture_nexus(&dirs, &rec(&mapped, "311", "90311")).unwrap();
        receipt::set_manual_source(&dirs, ModKind::Kiln, "mapped", &github("player/choice"), false, opts().now).unwrap();

        let r = check_targets(&dirs, vec![authored, from_nexus, mapped], &[&gh, &nx], &opts(), &mut NoProgress).unwrap();
        let s = of(&r, "authored").source.clone().unwrap();
        assert_eq!((s.provider, s.destination.as_str(), s.origin), (Provider::Github, "author/authored", SourceOrigin::Manifest));
        let s = of(&r, "from-nexus").source.clone().unwrap();
        assert_eq!((s.provider, s.destination.as_str(), s.origin), (Provider::Nexus, "enshrouded/310/90310", SourceOrigin::Receipt));
        let s = of(&r, "mapped").source.clone().unwrap();
        assert_eq!((s.provider, s.destination.as_str(), s.origin), (Provider::Github, "player/choice", SourceOrigin::Manual));
        // each provider was asked once, about its own mods only, with the receipt beside the item
        let asked_gh = gh.1.lock().unwrap().clone();
        assert_eq!(asked_gh.len(), 1);
        assert_eq!(asked_gh[0].iter().map(|i| i.target.library_ref.id.as_str()).collect::<Vec<_>>(), ["authored", "mapped"]);
        assert!(asked_gh[0][0].receipt.is_none() && asked_gh[0][1].receipt.is_some());
        let asked_nx = nx.1.lock().unwrap().clone();
        assert_eq!(asked_nx[0].iter().map(|i| i.target.library_ref.id.as_str()).collect::<Vec<_>>(), ["from-nexus"]);
        // results come back in the mods' order, one each
        assert_eq!(r.mods.iter().map(|m| m.library_ref.id.as_str()).collect::<Vec<_>>(), ["authored", "from-nexus", "mapped"]);
        let _ = std::fs::remove_dir_all(root);
    }

    #[test]
    fn a_changed_source_is_a_conflict_until_the_player_confirms_it() {
        let (dirs, root) = tmp("conflict");
        let gh = Recorder(Provider::Github, Mutex::default());
        let v1 = target(ModKind::Kiln, "glow", "1.0.0", Some(github("author/glow")));
        let r = check_targets(&dirs, vec![v1.clone()], &[&gh], &opts(), &mut NoProgress).unwrap();
        assert_eq!(of(&r, "glow").state, UpdateState::Current);
        // the next version's manifest names a fork's repository
        let v2 = target(ModKind::Kiln, "glow", "1.1.0", Some(github("someone-else/glow")));
        let r = check_targets(&dirs, vec![v2.clone()], &[&gh], &opts(), &mut NoProgress).unwrap();
        let s = of(&r, "glow");
        assert_eq!((s.state, s.reason), (UpdateState::Unknown, Some(UnknownReason::SourceConflict)));
        assert_eq!(s.source.as_ref().unwrap().destination, "someone-else/glow", "the status names the source that waits");
        assert_eq!(gh.1.lock().unwrap().len(), 1, "the new place is not asked before it is confirmed");
        // still a conflict the next time; the old version is still fine
        let r = check_targets(&dirs, vec![v2.clone()], &[&gh], &opts(), &mut NoProgress).unwrap();
        assert_eq!(of(&r, "glow").reason, Some(UnknownReason::SourceConflict));
        let r = check_targets(&dirs, vec![v1], &[&gh], &opts(), &mut NoProgress).unwrap();
        assert_eq!(of(&r, "glow").state, UpdateState::Current);
        // confirmed: checked there from now on
        assert_eq!(confirm_source(&dirs, &v2, opts().now).unwrap().unwrap().destination, "someone-else/glow");
        let r = check_targets(&dirs, vec![v2], &[&gh], &opts(), &mut NoProgress).unwrap();
        assert_eq!(of(&r, "glow").state, UpdateState::Current);
        // only where it is published changed? no: a new tag pattern at the same repository is no conflict
        let v3 = target(ModKind::Kiln, "glow", "1.2.0", Some(UpdatesSpec::Github {
            repository: "Someone-Else/Glow".into(), tag_pattern: "glow-{version}".into(), asset: None, channel: UpdateChannel::Prerelease }));
        let r = check_targets(&dirs, vec![v3], &[&gh], &opts(), &mut NoProgress).unwrap();
        assert_eq!(of(&r, "glow").state, UpdateState::Current);
        let _ = std::fs::remove_dir_all(root);
    }

    #[test]
    fn a_provider_that_is_not_there_makes_its_mods_disabled() {
        let (dirs, root) = tmp("disabled");
        let nx = Recorder(Provider::Nexus, Mutex::default());
        let t = target(ModKind::Kiln, "glow", "1.0.0", Some(github("author/glow")));
        let r = check_targets(&dirs, vec![t], &[&nx], &opts(), &mut NoProgress).unwrap();
        let s = of(&r, "glow");
        assert_eq!((s.state, s.reason, s.source.is_some()), (UpdateState::Disabled, None, true));
        assert!(nx.1.lock().unwrap().is_empty());
        let _ = std::fs::remove_dir_all(root);
    }

    /// A provider with a bug cannot make a failure read as "up to date".
    #[test]
    fn an_inconsistent_answer_is_not_trusted() {
        struct Liar;
        impl UpdateProvider for Liar {
            fn provider(&self) -> Provider {
                Provider::Github
            }
            fn check(&self, items: &[CheckItem], _: &CheckOptions, _: &mut dyn Progress) -> Result<Vec<ModUpdateStatus>> {
                Ok(items.iter().map(|i| { let mut s = i.status(false).with_state(UpdateState::Current); s.stale = true; s }).collect())
            }
        }
        let (dirs, root) = tmp("liar");
        let t = target(ModKind::Kiln, "glow", "1.0.0", Some(github("author/glow")));
        let r = check_targets(&dirs, vec![t], &[&Liar], &opts(), &mut NoProgress).unwrap();
        assert_eq!((of(&r, "glow").state, of(&r, "glow").reason), (UpdateState::Unknown, Some(UnknownReason::NotChecked)));
        let _ = std::fs::remove_dir_all(root);
    }

    struct Canceled;
    impl Progress for Canceled {
        fn report(&mut self, _: Option<f32>, _: &str) {}
        fn canceled(&self) -> bool {
            true
        }
    }

    #[test]
    fn a_canceled_check_asks_nobody() {
        let (dirs, root) = tmp("canceled");
        let gh = Recorder(Provider::Github, Mutex::default());
        let t = target(ModKind::Kiln, "glow", "1.0.0", Some(github("author/glow")));
        assert_eq!(check_targets(&dirs, vec![t], &[&gh], &opts(), &mut Canceled).unwrap_err().code, ErrorCode::Canceled);
        assert!(gh.1.lock().unwrap().is_empty());
        let _ = std::fs::remove_dir_all(root);
    }
}
